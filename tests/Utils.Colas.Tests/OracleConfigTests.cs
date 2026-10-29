using System.Text.Json;
using System.Text.RegularExpressions;
using Utils.Colas.Infrastructure;

namespace Utils.Colas.Tests;

public class OracleConfigTests
{
    [Theory]
    [InlineData(12154, "TNS", "Identifiant de connexion Oracle introuvable")]
    [InlineData(12541, "listener", "Aucun listener")]
    [InlineData(12514, "service", "Service Oracle inconnu")]
    [InlineData(12545, "host", "Hôte Oracle introuvable")]
    [InlineData(12170, "timeout", "injoignable")]
    [InlineData(1017, "invalid username/password", "secrets.json")]
    [InlineData(28001, "expired", "expiré")]
    [InlineData(942, "table or view does not exist", "Table ou vue inaccessible")]
    public void Describe_maps_known_codes(int number, string message, string expected)
    {
        var text = OracleErrors.Describe(number, message, "DV920");
        Assert.Contains(expected, text);
        Assert.Contains("DV920", text);
        Assert.Contains($"ORA-{number:D5}", text);
    }

    [Fact]
    public void Describe_unknown_code_keeps_oracle_message()
    {
        var text = OracleErrors.Describe(1234, "ORA-01234: autre", null);
        Assert.Equal("ORA-01234: autre", text);
    }

    [Fact]
    public void Describe_never_exposes_connection_string()
    {
        var text = OracleErrors.Describe(1017, "ORA-01017", "DV920");
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User Id", text, StringComparison.OrdinalIgnoreCase);
    }

    // Sans TnsAdmin, chaque DataSource doit être une adresse EZConnect (host:port/service), sinon ORA-12154.
    [Fact]
    public void Shipped_appsettings_use_ezconnect_when_no_tnsadmin()
    {
        var path = Path.Combine(FindRepoRoot(), "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var conns = doc.RootElement.GetProperty("OracleConnections");
        var ez = new Regex(@"^[\w.\-]+:\d+/[\w.\-]+$");
        var count = 0;
        foreach (var c in conns.EnumerateObject())
        {
            var tns = c.Value.TryGetProperty("TnsAdmin", out var t) ? t.GetString() : null;
            if (!string.IsNullOrWhiteSpace(tns)) continue;
            foreach (var key in new[] { "DataSource", "SysDataSource" })
                if (c.Value.TryGetProperty(key, out var ds) && !string.IsNullOrWhiteSpace(ds.GetString()))
                {
                    Assert.Matches(ez, ds.GetString()!);
                    count++;
                }
        }
        Assert.True(count > 0);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Utils.Colas.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Utils.Colas.csproj introuvable");
    }
}
