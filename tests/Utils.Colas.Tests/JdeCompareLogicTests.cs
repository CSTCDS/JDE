using System.Text.Json;
using Utils.Colas.Modules.JdeCompare;

namespace Utils.Colas.Tests;

public class JdeCompareLogicTests
{
    [Theory]
    [InlineData("125001", "01/01/2025")]
    [InlineData("99365", "31/12/1999")]
    [InlineData("124366", "31/12/2024")]
    [InlineData("0", "")]
    [InlineData("", "")]
    [InlineData("abc", "abc")]
    [InlineData("125400", "125400")]
    public void JulianToDate(string input, string expected) => Assert.Equal(expected, JdeDates.JulianToDate(input));

    [Theory]
    [InlineData("93005", "09:30:05")]
    [InlineData("235959", "23:59:59")]
    [InlineData("0", "")]
    public void ToTime(string input, string expected) => Assert.Equal(expected, JdeDates.ToTime(input));

    private static F9861Info Info(string date, string time = "0") => new() { RawDate = date, RawTime = time, Date = JdeDates.JulianToDate(date) };

    [Fact]
    public void Compare_SameSpecs_IsIdentical()
    {
        var s = new SpecInfo { RecordCount = 3, BlobSize = 100, Hash = "h" };
        var t = new SpecInfo { RecordCount = 3, BlobSize = 100, Hash = "h" };
        Assert.Equal("=", SpecReader.CompareSpecs(s, t, Info("125001"), Info("125001"), "APPL").Code);
    }

    [Fact]
    public void Compare_SameSpecs_NewerSource_IsMoreRecent()
    {
        var s = new SpecInfo { RecordCount = 1, BlobSize = 1, Hash = "h" };
        Assert.Equal("↑", SpecReader.CompareSpecs(s, s, Info("125002"), Info("125001"), "APPL").Code);
    }

    [Fact]
    public void Compare_DifferentHash_IsDifferent()
    {
        var r = SpecReader.CompareSpecs(new SpecInfo { RecordCount = 1, Hash = "a" }, new SpecInfo { RecordCount = 2, Hash = "b" }, Info("1"), Info("1"), "UBE");
        Assert.Equal("≠", r.Code);
        Assert.Contains("Nb enreg: 1 vs 2", r.Summary);
    }

    [Fact]
    public void Compare_MissingTarget()
        => Assert.Equal("Cible absente", SpecReader.CompareSpecs(new SpecInfo { RecordCount = 1 }, new SpecInfo(), Info("1"), Info("1"), "BSFN").Label);

    [Fact]
    public void Compare_Error_Wins()
        => Assert.Equal("!", SpecReader.CompareSpecs(new SpecInfo { Error = "ORA-00942" }, new SpecInfo(), Info("1"), Info("1"), "TBLE").Code);

    [Theory]
    [InlineData("", "", "", "-")]
    [InlineData("DSTR", "125010", "125001", "↑")]
    [InlineData("DSTR", "125001", "125010", "✓")]
    [InlineData("DSTR", "125001", "125001", "=")]
    [InlineData("DSTR", "0", "125001", "?")]
    public void Compare_TypeWithoutSpecs_UsesF9861Dates(string type, string srcDate, string tgtDate, string expected)
    {
        var ignored = new SpecInfo { Ignored = true };
        Assert.Equal(expected, SpecReader.CompareSpecs(ignored, ignored, Info(srcDate), Info(tgtDate), type).Code);
    }

    [Fact]
    public void LineDiff_DetectsAddedRemovedModified()
    {
        var diff = LineDiff.Generate("a\nb\nc\nd", "a\nc\nX\nd\ne");
        var types = JsonSerializer.SerializeToElement(diff).EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToArray();
        Assert.Equal(["removed", "added", "added"], types);
    }

    [Fact]
    public void LineDiff_IdenticalContent_IsEmpty() => Assert.Empty(LineDiff.Generate("x\r\ny", "x\ny"));

    [Fact]
    public void LineDiff_IsTruncated()
    {
        var diff = LineDiff.Generate(string.Join('\n', Enumerable.Range(0, 20).Select(i => $"s{i}")), string.Join('\n', Enumerable.Range(0, 20).Select(i => $"t{i}")), max: 5);
        Assert.Equal(6, diff.Count);
        Assert.Equal("truncated", JsonSerializer.SerializeToElement(diff[^1]).GetProperty("type").GetString());
    }
}
