using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace Utils.Colas.Infrastructure;

public sealed record PackageFile(string Path, byte[] Content)
{
    public long Size => Content.LongLength;
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    public string Directory => Path.LastIndexOf('/') is var i and > 0 ? Path[..i] : "";
    public string Extension => System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Name);
    public string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(Content));
}

public sealed record PackageMatch(PackageFile File, string Rule);

public sealed record PackageFileDiff(string Path, string Status, long SourceSize, long TargetSize);

public sealed record PackageComparison(string Status, string Label, int Identical, IReadOnlyList<PackageFileDiff> Files)
{
    public bool HasDifferences => Status is "different" or "source-absente" or "cible-absente";
}

public sealed class PackageLimitException(string message) : Exception(message);

// Package JDE stocké dans F98780R.XMOMRBLOB : archive zip dont le contenu varie selon le type d'objet
// (include64/*.h, sources .c, specs.zip imbriqué...). Partagé par Lire .h et JDE Compare.
public static class JdePackage
{
    public const long MaxBlobBytes = 64L * 1024 * 1024;
    public const long MaxEntryBytes = 32L * 1024 * 1024;
    public const long MaxTotalBytes = 256L * 1024 * 1024;
    public const int MaxFiles = 5000;
    public const int MaxDepth = 2;

    // Lecture du BLOB (null si l'objet est absent de <schema>.F98780R). Le schéma doit être validé par l'appelant.
    public static async Task<byte[]?> ReadBlobAsync(OracleConnection conn, string schema, string objectName, int timeoutSeconds, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandTimeout = timeoutSeconds;
        cmd.CommandText = $"SELECT XMOMRBLOB FROM {schema}.F98780R WHERE TRIM(XMOMWOBJID) = :objid AND ROWNUM = 1";
        cmd.Parameters.Add(new OracleParameter("objid", OracleDbType.Varchar2, objectName, ParameterDirection.Input));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.IsDBNull(0)) return null;
        using OracleBlob blob = r.GetOracleBlob(0);
        if (blob.Length > MaxBlobBytes)
            throw new PackageLimitException($"Package {objectName} trop volumineux ({blob.Length / 1024 / 1024} Mo).");
        if (blob.Length == 0) return null;
        using var ms = new MemoryStream((int)blob.Length);
        await blob.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    // Liste à plat des fichiers ; les zip imbriqués (ex. specs.zip) sont développés : "specs.zip/chemin/fichier".
    public static IReadOnlyList<PackageFile> Open(byte[] zip)
    {
        var files = new List<PackageFile>();
        var budget = MaxTotalBytes;
        Walk(zip, "", 0, files, ref budget);
        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(byte[] data, string prefix, int depth, List<PackageFile> files, ref long budget)
    {
        using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        foreach (var e in archive.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;
            if (files.Count >= MaxFiles) throw new PackageLimitException($"Package : plus de {MaxFiles} fichiers.");
            var path = prefix + e.FullName.Replace('\\', '/').TrimStart('/');
            var bytes = ReadLimited(e, Math.Min(MaxEntryBytes, budget), path);
            budget -= bytes.Length;

            if (depth < MaxDepth && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var nested = new List<PackageFile>();
                var nestedBudget = budget;
                try
                {
                    Walk(bytes, path + "/", depth + 1, nested, ref nestedBudget);
                    if (files.Count + nested.Count > MaxFiles) throw new PackageLimitException($"Package : plus de {MaxFiles} fichiers.");
                    files.AddRange(nested);
                    budget = nestedBudget;
                    continue;
                }
                catch (InvalidDataException)
                {
                    // .zip illisible : conservé comme fichier ordinaire.
                }
            }
            files.Add(new PackageFile(path, bytes));
        }
    }

    private static byte[] ReadLimited(ZipArchiveEntry entry, long limit, string path)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > limit) throw new PackageLimitException($"Package : {path} dépasse la taille autorisée.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static bool InNestedZip(PackageFile f) =>
        f.Directory.Split('/').Any(seg => seg.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    private static bool InSpecsZip(PackageFile f) =>
        f.Directory.Split('/').Any(seg => seg.Equals("specs.zip", StringComparison.OrdinalIgnoreCase));

    // Fichier à afficher : include64/<objet>.h, sinon un .h/.c d'un autre dossier, sinon <objet>.* dans specs.zip.
    public static PackageMatch? FindMain(IReadOnlyList<PackageFile> files, string objectName)
    {
        var top = files.Where(f => !InNestedZip(f)).ToList();

        var include = top.FirstOrDefault(f =>
            f.Directory.Split('/')[^1].Equals("include64", StringComparison.OrdinalIgnoreCase) &&
            f.Name.Equals(objectName + ".h", StringComparison.OrdinalIgnoreCase));
        if (include is not null) return new(include, "include64");

        var source = top.Where(f => f.Extension is ".h" or ".c")
            .OrderBy(f => f.Stem.Equals(objectName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Extension == ".h" ? 0 : 1)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (source is not null) return new(source, "source");

        var spec = files.Where(InSpecsZip)
            .Where(f => f.Name.Contains(objectName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Stem.Equals(objectName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return spec is null ? null : new(spec, "specs.zip");
    }

    public static PackageFile? Find(IReadOnlyList<PackageFile> files, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null
            : files.FirstOrDefault(f => f.Path.Equals(path.Replace('\\', '/').Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsText(byte[] b)
    {
        if (HasUtf16Bom(b)) return true;
        var n = Math.Min(b.Length, 8000);
        for (var i = 0; i < n; i++) if (b[i] == 0) return false;
        return true;
    }

    private static bool HasUtf16Bom(byte[] b) => b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF));

    // UTF-8 (avec ou sans BOM), UTF-16 avec BOM, sinon Latin-1 (sources JDE ANSI).
    public static string Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
    }

    public static string HexDump(byte[] b, int max = 4096)
    {
        var sb = new StringBuilder();
        var n = Math.Min(b.Length, max);
        for (var off = 0; off < n; off += 16)
        {
            var len = Math.Min(16, n - off);
            sb.Append(off.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++) sb.Append(i < len ? b[off + i].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (var i = 0; i < len; i++) { var c = b[off + i]; sb.Append(c is >= 32 and < 127 ? (char)c : '.'); }
            sb.Append('\n');
        }
        if (b.Length > max) sb.Append($"... ({b.Length} octets au total)\n");
        return sb.ToString();
    }

    // Comparaison fichier par fichier sur le contenu (les dates des entrées zip sont ignorées).
    // Un fichier texte dont seules les valeurs SIUPMJ/SIUPMT (date/heure de mise à jour JDE) diffèrent est identique.
    public static PackageComparison Compare(IReadOnlyList<PackageFile>? source, IReadOnlyList<PackageFile>? target)
    {
        if (source is null && target is null) return new("absent", "Absent des deux environnements", 0, []);
        if (source is null) return new("source-absente", "Package absent en source", 0, target!.Select(f => new PackageFileDiff(f.Path, "cible-seule", 0, f.Size)).ToList());
        if (target is null) return new("cible-absente", "Package absent en cible", 0, source.Select(f => new PackageFileDiff(f.Path, "source-seule", f.Size, 0)).ToList());

        var tgt = target.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var diffs = new List<PackageFileDiff>();
        var identical = 0;
        foreach (var s in source)
        {
            if (!tgt.Remove(s.Path, out var t)) diffs.Add(new(s.Path, "source-seule", s.Size, 0));
            else if (s.Sha256 != t.Sha256 && !SameIgnoringUpdateStamps(s, t)) diffs.Add(new(s.Path, "modifie", s.Size, t.Size));
            else identical++;
        }
        diffs.AddRange(tgt.Values.Select(t => new PackageFileDiff(t.Path, "cible-seule", 0, t.Size)));
        diffs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return diffs.Count == 0
            ? new("identique", $"Package identique ({identical} fichier(s))", identical, diffs)
            : new("different", $"Package : {diffs.Count} fichier(s) différent(s), {identical} identique(s)", identical, diffs);
    }

    // Valeur d'un champ SIUPMJ/SIUPMT : <SIUPMJ>v</SIUPMJ>, SIUPMJ="v", SIUPMJ=v, "SIUPMJ":"v", name="SIUPMJ" value="v".
    private static readonly Regex UpdateStampRegex = new(
        @"\b(SIUPM[JT])\b(""?'?[ \t]*(?:[=:>]|[ \t]value[ \t]*=)[ \t]*[""']?)[^<""'\s,;]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Efface les valeurs SIUPMJ/SIUPMT (le nombre de lignes est conservé).
    public static string MaskUpdateStamps(string text) => UpdateStampRegex.Replace(text, "$1$2");

    private static bool SameIgnoringUpdateStamps(PackageFile s, PackageFile t)
    {
        if (!IsText(s.Content) || !IsText(t.Content)) return false;
        string a = Decode(s.Content), b = Decode(t.Content);
        if (!UpdateStampRegex.IsMatch(a) && !UpdateStampRegex.IsMatch(b)) return false;
        return MaskUpdateStamps(a) == MaskUpdateStamps(b);
    }
}
