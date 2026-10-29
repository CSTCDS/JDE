using System.Data;
using Oracle.ManagedDataAccess.Client;
using Utils.Colas.Infrastructure;

namespace Utils.Colas.Modules.JdeCompare;

public sealed class F9861Info
{
    public string User { get; set; } = "";
    public string Date { get; set; } = "";
    public string Time { get; set; } = "";
    public string RawDate { get; set; } = "";
    public string RawTime { get; set; } = "";
}

public sealed class SpecInfo
{
    public int RecordCount { get; set; }
    public long BlobSize { get; set; }
    public string Hash { get; set; } = "";
    public string ExtraInfo { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Ignored { get; set; }
}

public sealed record CompareResult(string Code, string Label, string Summary);

// Lecture des specs JDE (repris de NERcompare). Les noms de table/colonne sont des constantes ;
// les schémas proviennent de la configuration validée au démarrage.
internal sealed class SpecReader(Func<OracleConnection, string, OracleCommand> cmd, string ol)
{
    private static OracleParameter P(string name, string value) => new(name, OracleDbType.Varchar2, value, ParameterDirection.Input);

    public async Task<F9861Info> GetF9861Info(OracleConnection conn, string objectName, string envSchema)
    {
        var info = new F9861Info();
        try
        {
            await using var c = cmd(conn, $@"SELECT SIUSER, SIUPMJ, SIUPMT FROM {ol}.F9861
WHERE TRIM(SIOBNM) = TRIM(:obj) AND TRIM(SIPATHCD) = TRIM(:env) FETCH FIRST 1 ROWS ONLY");
            c.Parameters.Add(P("obj", objectName));
            c.Parameters.Add(P("env", envSchema));
            await using var r = await c.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                info.User = r.IsDBNull(0) ? "" : r.GetValue(0).ToString()!.Trim();
                info.RawDate = r.IsDBNull(1) ? "" : r.GetValue(1).ToString()!.Trim();
                info.RawTime = r.IsDBNull(2) ? "" : r.GetValue(2).ToString()!.Trim();
                info.Date = JdeDates.JulianToDate(info.RawDate);
                info.Time = JdeDates.ToTime(info.RawTime);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            info.User = $"Erreur: {ex.Message}";
        }
        return info;
    }

    public async Task<SpecInfo> GetSpecInfo(OracleConnection conn, string schema, string objectName, string objectType)
    {
        var spec = new SpecInfo();
        try
        {
            switch (objectType.Trim().ToUpperInvariant())
            {
                case "TBLE":
                    spec = await CountAndHash(conn, schema, "F98711", "TDOBNM", objectName, "TDOBND");
                    var index = await CountAndHash(conn, schema, "F98713", "TLOBNM", objectName, "TLOBND");
                    spec.RecordCount += index.RecordCount;
                    spec.ExtraInfo = $"Détails: {spec.RecordCount - index.RecordCount}, Index: {index.RecordCount}";
                    break;

                case "BSVW":
                    spec = await CountAndHashBlob(conn, schema, "F98720", "BVOBNM", objectName, "BVBVBLOB", null);
                    spec.ExtraInfo = $"F98720: {spec.RecordCount} enreg ({spec.BlobSize} octets)";
                    break;

                case "BSFN":
                    spec = Sum(
                        ("F98762", await CountAndHashBlob(conn, schema, "F98762", "BCOBNM", objectName, "BCRDABLOB", null)),
                        ("F98741(via F9862)", await CountAndHashBlobViaF9862(conn, schema, objectName)));
                    break;

                case "APPL":
                case "NER":
                    spec = Sum(
                        ("F98751", await CountAndHashBlob(conn, schema, "F98751", "FSOBNM", objectName, "FSFDABLOB", null)),
                        ("F98750", await CountAndHashBlob(conn, schema, "F98750", "FTOBNM", objectName, "FTFDABLOB", null)),
                        ("F98740", await CountAndHashBlob(conn, schema, "F98740", "ELOBNM", objectName, "ELERBLOB", null)),
                        ("F98741(F9862)", await CountAndHashBlobViaF9862(conn, schema, objectName)),
                        ("F98743", await CountAndHashBlob(conn, schema, "F98743", "DTOBNM", objectName, "DTERBLOB", null)));
                    break;

                case "UBE":
                    spec = Sum(
                        ("F98760", await CountAndHashBlob(conn, schema, "F98760", "RTOBNM", objectName, "RTRDABLOB", "RTVERS")),
                        ("F98761", await CountAndHashBlob(conn, schema, "F98761", "RSOBNM", objectName, "RSRDABLOB", "RSVERS")),
                        ("F98740", await CountAndHashBlob(conn, schema, "F98740", "ELOBNM", objectName, "ELERBLOB", null)),
                        ("F98741(F9862)", await CountAndHashBlobViaF9862(conn, schema, objectName)),
                        ("F98743", await CountAndHashBlob(conn, schema, "F98743", "DTOBNM", objectName, "DTERBLOB", null)));
                    break;

                default:
                    spec.Ignored = true;
                    spec.ExtraInfo = $"Type '{objectType.Trim().ToUpperInvariant()}' - pas de table specs connue";
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            spec.Error = ex.Message;
        }
        return spec;
    }

    private static SpecInfo Sum(params (string Table, SpecInfo Spec)[] parts) => new()
    {
        RecordCount = parts.Sum(p => p.Spec.RecordCount),
        BlobSize = parts.Sum(p => p.Spec.BlobSize),
        Hash = string.Join("|", parts.Select(p => p.Spec.Hash)),
        ExtraInfo = string.Join(", ", parts.Select(p => $"{p.Table}:{p.Spec.RecordCount}"))
    };

    private async Task<SpecInfo> CountAndHash(OracleConnection conn, string schema, string table, string keyCol, string objectName, string infoCol)
    {
        var spec = new SpecInfo();
        await using (var c = cmd(conn, $"SELECT COUNT(*), SUM(LENGTH({infoCol})) FROM {schema}.{table} WHERE TRIM({keyCol}) = TRIM(:obj)"))
        {
            c.Parameters.Add(P("obj", objectName));
            await using var r = await c.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                spec.RecordCount = r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0));
                spec.BlobSize = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1));
            }
        }

        await using var h = cmd(conn, $@"SELECT ORA_HASH(LISTAGG({infoCol}, '|') WITHIN GROUP (ORDER BY {infoCol}))
FROM {schema}.{table} WHERE TRIM({keyCol}) = TRIM(:obj)");
        h.Parameters.Add(P("obj", objectName));
        try
        {
            spec.Hash = (await h.ExecuteScalarAsync())?.ToString() ?? "";
        }
        catch (OracleException)
        {
            // LISTAGG > 4000 caractères : comparaison sur nombre + taille
            spec.Hash = $"{spec.RecordCount}_{spec.BlobSize}";
        }
        return spec;
    }

    // versCol non null : filtre version '+' (UBE)
    private async Task<SpecInfo> CountAndHashBlob(OracleConnection conn, string schema, string table, string keyCol, string objectName, string blobCol, string? versCol)
    {
        var spec = new SpecInfo();
        var where = $"TRIM({keyCol}) = TRIM(:obj)" + (versCol is null ? "" : $" AND TRIM({versCol}) = '+'");
        try
        {
            await using var c = cmd(conn, $"SELECT COUNT(*), NVL(SUM(DBMS_LOB.GETLENGTH({blobCol})),0) FROM {schema}.{table} WHERE {where}");
            c.Parameters.Add(P("obj", objectName));
            await using var r = await c.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                spec.RecordCount = r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0));
                spec.BlobSize = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1));
            }
        }
        catch (OracleException)
        {
            // DBMS_LOB.GETLENGTH impossible (LONG RAW...) : comptage seul
            await using var c = cmd(conn, $"SELECT COUNT(*) FROM {schema}.{table} WHERE {where}");
            c.Parameters.Add(P("obj", objectName));
            await using var r = await c.ExecuteReaderAsync();
            if (await r.ReadAsync()) spec.RecordCount = r.IsDBNull(0) ? 0 : Convert.ToInt32(r.GetValue(0));
        }

        await using var h = cmd(conn, $"SELECT ORA_HASH(COUNT(*) || ':' || NVL(SUM(DBMS_LOB.GETLENGTH({blobCol})),0)) FROM {schema}.{table} WHERE {where}");
        h.Parameters.Add(P("obj", objectName));
        try
        {
            spec.Hash = (await h.ExecuteScalarAsync())?.ToString() ?? "";
        }
        catch (OracleException)
        {
            spec.Hash = $"{spec.RecordCount}_{spec.BlobSize}";
        }
        return spec;
    }

    // F98741 via F9862 : SIOBNM -> SIEVSK -> F98741.ESEVSK
    private async Task<SpecInfo> CountAndHashBlobViaF9862(OracleConnection conn, string schema, string objectName)
    {
        var spec = new SpecInfo();
        var evskList = new List<string>();
        await using (var c = cmd(conn, $"SELECT SIEVSK FROM {ol}.F9862 WHERE TRIM(SIOBNM) = TRIM(:obj)"))
        {
            c.Parameters.Add(P("obj", objectName));
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
                if (!r.IsDBNull(0) && r.GetString(0).Trim() is { Length: > 0 } evsk) evskList.Add(evsk);
        }

        var hashes = new List<string>();
        foreach (var evsk in evskList)
        {
            var part = await CountAndHashBlob(conn, schema, "F98741", "ESEVSK", evsk, "ESERBLOB", null);
            spec.RecordCount += part.RecordCount;
            spec.BlobSize += part.BlobSize;
            hashes.Add($"{evsk}:{part.RecordCount}:{part.BlobSize}");
        }
        spec.Hash = string.Join("|", hashes);
        return spec;
    }

    public static CompareResult CompareSpecs(SpecInfo src, SpecInfo tgt, F9861Info srcInfo, F9861Info tgtInfo, string objectType)
    {
        if (!string.IsNullOrWhiteSpace(src.Error) || !string.IsNullOrWhiteSpace(tgt.Error))
            return new("!", "Erreur", $"Source: {(string.IsNullOrWhiteSpace(src.Error) ? "OK" : src.Error)} / Cible: {(string.IsNullOrWhiteSpace(tgt.Error) ? "OK" : tgt.Error)}");

        if (src.Ignored && tgt.Ignored)
        {
            if (string.IsNullOrWhiteSpace(objectType))
                return new("-", "Non applicable", src.ExtraInfo);

            // Type sans table specs : comparaison sur les dates F9861
            var srcHas = !string.IsNullOrWhiteSpace(srcInfo.RawDate) && srcInfo.RawDate != "0";
            var tgtHas = !string.IsNullOrWhiteSpace(tgtInfo.RawDate) && tgtInfo.RawDate != "0";
            if (!srcHas && !tgtHas) return new("?", "Absent", $"Aucun enregistrement F9861 — {src.ExtraInfo}");
            if (!srcHas) return new("?", "Source absente", $"Absent en source (F9861), présent en cible ({tgtInfo.Date}) — {src.ExtraInfo}");
            if (!tgtHas) return new("?", "Cible absente", $"Présent en source ({srcInfo.Date}), absent en cible (F9861) — {src.ExtraInfo}");
            if (IsMoreRecent(srcInfo, tgtInfo)) return new("↑", "Plus récent", $"Source plus récente ({srcInfo.Date} > {tgtInfo.Date}) — {src.ExtraInfo}");
            if (IsMoreRecent(tgtInfo, srcInfo)) return new("✓", "Conforme", $"Cible plus récente ({tgtInfo.Date} > {srcInfo.Date}) — {src.ExtraInfo}");
            return new("=", "Identique", $"Mêmes dates F9861 — {src.ExtraInfo}");
        }

        if (src.RecordCount == 0 && tgt.RecordCount == 0) return new("?", "Absent", "Objet absent des deux environnements");
        if (src.RecordCount == 0) return new("?", "Source absente", $"Absent en source, {tgt.RecordCount} enreg en cible");
        if (tgt.RecordCount == 0) return new("?", "Cible absente", $"{src.RecordCount} enreg en source, absent en cible");

        if (src.Hash == tgt.Hash && src.BlobSize == tgt.BlobSize && src.RecordCount == tgt.RecordCount)
        {
            return IsMoreRecent(srcInfo, tgtInfo)
                ? new("↑", "Plus récent", $"Identique en contenu mais source plus récente ({srcInfo.Date} > {tgtInfo.Date})")
                : new("=", "Identique", $"{src.RecordCount} enregistrement(s), taille identique");
        }

        var diffs = new List<string>();
        if (src.RecordCount != tgt.RecordCount) diffs.Add($"Nb enreg: {src.RecordCount} vs {tgt.RecordCount}");
        if (src.BlobSize != tgt.BlobSize) diffs.Add($"Taille: {src.BlobSize} vs {tgt.BlobSize} octets");
        if (src.Hash != tgt.Hash) diffs.Add("Contenu différent");
        return new("≠", "Différent", string.Join(" | ", diffs));
    }

    // Résultat global = specs + package F98780R : un package différent rend l'objet « Différent » si les specs sont identiques.
    public static CompareResult MergePackage(CompareResult specs, PackageComparison pkg)
    {
        if (pkg.Status == "absent") return specs;
        var summary = string.IsNullOrWhiteSpace(specs.Summary) ? pkg.Label : $"{specs.Summary} | {pkg.Label}";
        if (pkg.HasDifferences && specs.Code == "=")
            return new("≠", "Différent", summary);
        return specs with { Summary = summary };
    }

    // Dates julien JDE brutes (CYYDDD) puis heure HHMMSS
    public static bool IsMoreRecent(F9861Info a, F9861Info b)
    {
        if (!int.TryParse(a.RawDate, out var da) || !int.TryParse(b.RawDate, out var db)) return false;
        if (da != db) return da > db;
        return int.TryParse(a.RawTime, out var ta) && int.TryParse(b.RawTime, out var tb) && ta > tb;
    }
}

public static class JdeDates
{
    // CYYDDD : C=siècle (0=19xx, 1=20xx), YY=année, DDD=jour de l'année
    public static string JulianToDate(string jdeJulian)
    {
        if (string.IsNullOrWhiteSpace(jdeJulian)) return "";
        if (!int.TryParse(jdeJulian, out var j)) return jdeJulian;
        if (j <= 0) return "";
        var year = (j / 100000 == 0 ? 1900 : 2000) + (j % 100000) / 1000;
        var day = j % 1000;
        if (day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365)) return jdeJulian;
        return new DateTime(year, 1, 1).AddDays(day - 1).ToString("dd/MM/yyyy");
    }

    // HHMMSS (entier)
    public static string ToTime(string jdeTime)
    {
        if (string.IsNullOrWhiteSpace(jdeTime)) return "";
        if (!int.TryParse(jdeTime, out var t)) return jdeTime;
        if (t <= 0) return "";
        return $"{t / 10000:D2}:{t % 10000 / 100:D2}:{t % 100:D2}";
    }
}

public static class LineDiff
{
    // Diff séquentiel simple (lignes ajoutées / supprimées / modifiées), limité à 500 entrées.
    public static List<object> Generate(string srcContent, string tgtContent, int max = 500)
    {
        var s = srcContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var t = tgtContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var diff = new List<object>();
        int si = 0, ti = 0;
        while (si < s.Length || ti < t.Length)
        {
            if (si >= s.Length) { diff.Add(new { type = "added", line = ti + 1, content = t[ti] }); ti++; }
            else if (ti >= t.Length) { diff.Add(new { type = "removed", line = si + 1, content = s[si] }); si++; }
            else if (s[si] == t[ti]) { si++; ti++; }
            else
            {
                var inTgt = Array.IndexOf(t, s[si], ti + 1);
                var inSrc = Array.IndexOf(s, t[ti], si + 1);
                if (inTgt >= 0 && (inSrc < 0 || inTgt - ti <= inSrc - si))
                    while (ti < inTgt) { diff.Add(new { type = "added", line = ti + 1, content = t[ti] }); ti++; }
                else if (inSrc >= 0)
                    while (si < inSrc) { diff.Add(new { type = "removed", line = si + 1, content = s[si] }); si++; }
                else { diff.Add(new { type = "modified", line = si + 1, srcContent = s[si], tgtContent = t[ti] }); si++; ti++; }
            }
            if (diff.Count >= max)
            {
                diff.Add(new { type = "truncated", line = 0, content = $"... diff tronqué ({Math.Max(s.Length, t.Length)} lignes au total)" });
                break;
            }
        }
        return diff;
    }
}
