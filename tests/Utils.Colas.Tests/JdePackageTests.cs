using System.IO.Compression;
using System.Text;
using Utils.Colas.Infrastructure;
using Utils.Colas.Modules.JdeCompare;

namespace Utils.Colas.Tests;

public class JdePackageTests
{
    private static byte[] Zip(params (string Path, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var s = z.CreateEntry(path).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private static byte[] T(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void FindMain_PrefersInclude64Header()
    {
        var files = JdePackage.Open(Zip(("include64/F55CST.h", T("h")), ("source64/F55CST.c", T("c")), ("include64/OTHER.h", T("o"))));
        var m = JdePackage.FindMain(files, "F55CST");
        Assert.Equal("include64/F55CST.h", m!.File.Path);
        Assert.Equal("include64", m.Rule);
    }

    [Fact]
    public void FindMain_WithoutInclude64_TakesHOrCFromAnotherFolder()
    {
        var files = JdePackage.Open(Zip(("source64/B55X.c", T("c")), ("other/readme.txt", T("x")), ("include/B55X.h", T("h"))));
        var m = JdePackage.FindMain(files, "B55X");
        Assert.Equal("include/B55X.h", m!.File.Path);
        Assert.Equal("source", m.Rule);

        var onlyC = JdePackage.FindMain(JdePackage.Open(Zip(("src/whatever.c", T("c")))), "B55X");
        Assert.Equal("src/whatever.c", onlyC!.File.Path);
    }

    [Fact]
    public void FindMain_LooksInsideNestedSpecsZip()
    {
        var specs = Zip(("spec/P55CST.xml", T("<spec/>")), ("spec/F0101.xml", T("<x/>")));
        var files = JdePackage.Open(Zip(("specs.zip", specs), ("manifest.xml", T("<m/>"))));
        Assert.Contains(files, f => f.Path == "specs.zip/spec/P55CST.xml");
        var m = JdePackage.FindMain(files, "P55CST");
        Assert.Equal("specs.zip/spec/P55CST.xml", m!.File.Path);
        Assert.Equal("specs.zip", m.Rule);
    }

    [Fact]
    public void FindMain_NothingSuitable_ReturnsNull_AndTreeIsAvailable()
    {
        var files = JdePackage.Open(Zip(("manifest.xml", T("<m/>")), ("specs.zip", Zip(("a/OTHER.xml", T("x"))))));
        Assert.Null(JdePackage.FindMain(files, "R55TEST"));
        Assert.Equal(2, files.Count);
        Assert.NotNull(JdePackage.Find(files, "specs.zip/a/OTHER.xml"));
    }

    [Fact]
    public void Open_InvalidNestedZip_IsKeptAsFile()
    {
        var files = JdePackage.Open(Zip(("specs.zip", T("pas un zip"))));
        Assert.Equal("specs.zip", Assert.Single(files).Path);
    }

    [Fact]
    public void Decode_HandlesUtf8Latin1AndUtf16()
    {
        Assert.Equal("é", JdePackage.Decode(T("é")));
        Assert.Equal("é", JdePackage.Decode([0xE9]));
        Assert.Equal("é", JdePackage.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("é")]));
        Assert.True(JdePackage.IsText([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("abc")]));
        Assert.False(JdePackage.IsText([1, 0, 2]));
    }

    [Fact]
    public void Compare_ReportsEveryFileDifference_IgnoringZipDates()
    {
        var src = JdePackage.Open(Zip(("include64/X.h", T("a")), ("same.txt", T("s")), ("only-src.c", T("1")), ("specs.zip", Zip(("s/X.xml", T("v1"))))));
        var tgt = JdePackage.Open(Zip(("include64/X.h", T("b")), ("same.txt", T("s")), ("only-tgt.c", T("2")), ("specs.zip", Zip(("s/X.xml", T("v1"))))));
        var cmp = JdePackage.Compare(src, tgt);
        Assert.Equal("different", cmp.Status);
        Assert.Equal(2, cmp.Identical);
        Assert.Equal(
            [("include64/X.h", "modifie"), ("only-src.c", "source-seule"), ("only-tgt.c", "cible-seule")],
            cmp.Files.Select(f => (f.Path, f.Status)).ToArray());

        Assert.Equal("identique", JdePackage.Compare(src, src).Status);
        Assert.Equal("cible-absente", JdePackage.Compare(src, null).Status);
        Assert.Equal("absent", JdePackage.Compare(null, null).Status);
    }

    [Fact]
    public void MergePackage_TurnsIdenticalSpecsIntoDifferent()
    {
        var diff = new PackageComparison("different", "Package : 1 fichier(s) différent(s)", 0, [new("a.h", "modifie", 1, 2)]);
        var merged = SpecReader.MergePackage(new("=", "Identique", "3 enregistrement(s)"), diff);
        Assert.Equal("≠", merged.Code);
        Assert.Contains("Package", merged.Summary);

        var same = SpecReader.MergePackage(new("=", "Identique", "ok"), new("identique", "Package identique (2 fichier(s))", 2, []));
        Assert.Equal("=", same.Code);

        var absent = SpecReader.MergePackage(new("-", "Non applicable", "x"), new("absent", "Absent", 0, []));
        Assert.Equal("x", absent.Summary);
    }
}
