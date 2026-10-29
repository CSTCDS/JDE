using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Utils.Colas.Tests;

// Remplace Windows Authentication : authentifié sauf si l'en-tête X-Test-Anonymous est présent.
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey("X-Test-Anonymous")) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, @"COLAS\testeur")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class UtilsColasFactory : WebApplicationFactory<Program>
{
    public const string SecretPassword = "Sup3r-Secret-Pwd";

    // Negotiate exige Kestrel : on le retire sous TestServer (remplacé par TestAuthHandler).
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        host.Services.GetRequiredService<IAuthenticationSchemeProvider>().RemoveScheme(NegotiateDefaults.AuthenticationScheme);
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Identifiants factices pour DV920 uniquement ; PD920 reste sans identifiants.
            ["OracleConnections:DV920:UserId"] = "user",
            ["OracleConnections:DV920:Password"] = SecretPassword,
            ["OracleConnections:PD920:UserId"] = "",
            ["OracleConnections:PD920:Password"] = "",
            ["OracleConnections:ORPHAN:UserId"] = "user",
            ["OracleConnections:ORPHAN:Password"] = SecretPassword
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}

public class ApiTests(UtilsColasFactory factory) : IClassFixture<UtilsColasFactory>
{
    private HttpClient Client(bool anonymous = false)
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (anonymous) c.DefaultRequestHeaders.Add("X-Test-Anonymous", "1");
        return c;
    }

    // --- Authentification / autorisation ---

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var res = await Client(anonymous: true).GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/lireh/")]
    [InlineData("/jdecompare/")]
    [InlineData("/common/banner.js")]
    [InlineData("/api/connections")]
    [InlineData("/api/lireh/environments")]
    [InlineData("/api/jdecompare/environments")]
    public async Task Everything_RequiresAuthentication(string url)
    {
        var res = await Client(anonymous: true).GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task WindowsUser_ReturnsNameWithoutDomain()
    {
        var b = await Client().GetFromJsonAsync<JsonElement>("/api/windows-user");
        Assert.Equal("testeur", b.GetProperty("user").GetString());
    }

    [Fact]
    public async Task Version_ExposesReleaseId()
    {
        // Hors livraison build-update.ps1, aucun identifiant UPD n'est injecté.
        var b = await Client().GetFromJsonAsync<JsonElement>("/api/version");
        Assert.Equal("local", b.GetProperty("release").GetString());
        Assert.False(string.IsNullOrEmpty(b.GetProperty("version").GetString()));
    }

    // --- Cache et en-têtes de sécurité ---

    [Theory]
    [InlineData("/")]
    [InlineData("/lireh/")]
    [InlineData("/jdecompare/")]
    public async Task HtmlPages_AreNotCached(string url)
    {
        var res = await Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        var cc = res.Headers.CacheControl!;
        Assert.True(cc.NoStore && cc.NoCache && cc.MustRevalidate);
    }

    [Theory]
    [InlineData("/common/banner.js")]
    [InlineData("/jdecompare/js/app.js")]
    [InlineData("/jdecompare/css/site.css")]
    [InlineData("/config/utilities.json")]
    public async Task Assets_AreRevalidated(string url)
    {
        var res = await Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task SecurityHeaders_ArePresent()
    {
        var res = await Client().GetAsync("/");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(res.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task CorrelationId_IsEchoed()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("X-Correlation-ID", "abc-123");
        var res = await Client().SendAsync(req);
        Assert.Equal("abc-123", res.Headers.GetValues("X-Correlation-ID").Single());
    }

    // --- Menu ---

    [Fact]
    public async Task Menu_ListsLireHAndJdeCompare_AndEachPageExists()
    {
        var client = Client();
        var list = await client.GetFromJsonAsync<JsonElement[]>("/config/utilities.json");
        Assert.Equal(["lireh", "jdecompare"], list!.Select(u => u.GetProperty("id").GetString()!).ToArray());
        foreach (var u in list!)
        {
            var res = await client.GetAsync("/" + u.GetProperty("url").GetString());
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }

    [Fact]
    public async Task UtilityFolder_WithoutSlash_RedirectsToFolder()
    {
        var res = await Client().GetAsync("/lireh");
        Assert.True(res.StatusCode is HttpStatusCode.Moved or HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect);
        Assert.EndsWith("/lireh/", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Connections_DoNotLeakCredentials()
    {
        var body = await Client().GetStringAsync("/api/connections");
        Assert.DoesNotContain(UtilsColasFactory.SecretPassword, body);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PYI920", body);
    }

    // --- Lire .h ---

    [Fact]
    public async Task LireH_Environments_ComeFromOracleConnections_InOrder()
    {
        var list = await Client().GetFromJsonAsync<JsonElement[]>("/api/lireh/environments");
        Assert.Equal(["DV920", "PYI920", "QA920", "PD920"], list!.Select(e => e.GetProperty("code").GetString()!).ToArray());
        Assert.Equal("PY920", list![1].GetProperty("schema").GetString());
        Assert.True(list![0].GetProperty("available").GetBoolean());
        Assert.False(list![3].GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Connection_WithoutDataSource_IsIgnoredEverywhere()
    {
        // Clé orpheline (ex. ancien secrets.json de lire.h avec "E1") : ne doit apparaître dans aucune liste.
        var client = Client();
        Assert.DoesNotContain("ORPHAN", await client.GetStringAsync("/api/lireh/environments"));
        Assert.DoesNotContain("ORPHAN", await client.GetStringAsync("/api/jdecompare/environments"));
        Assert.DoesNotContain("ORPHAN", await client.GetStringAsync("/api/connections"));
    }

    [Theory]
    [InlineData("/api/lireh/header?env=DV920&objet=", "invalide")]
    [InlineData("/api/lireh/header?env=DV920&objet=F58'--", "invalide")]
    [InlineData("/api/lireh/header?env=DV920&objet=TROPLONGNOM123", "invalide")]
    [InlineData("/api/lireh/header?env=E9&objet=F0101", "inconnu")]
    [InlineData("/api/lireh/header?env=ORPHAN&objet=F0101", "inconnu")]
    [InlineData("/api/lireh/header?env=PD920&objet=F0101", "manquants")]
    public async Task LireH_Header_ValidatesInput(string url, string expected)
    {
        var res = await Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(expected, await res.Content.ReadAsStringAsync());
    }

    // --- JDE Compare ---

    [Fact]
    public async Task JdeCompare_Environments_AreMapped()
    {
        var list = await Client().GetFromJsonAsync<JsonElement[]>("/api/jdecompare/environments");
        Assert.Equal(["DV920", "PY920", "QA920", "PD920"], list!.Select(e => e.GetProperty("environment").GetString()!).ToArray());
        Assert.Equal("PYI920", list![1].GetProperty("connection").GetString());
    }

    [Fact]
    public async Task JdeCompare_ShortQuery_ReturnsEmptyWithoutDatabase()
    {
        var list = await Client().GetFromJsonAsync<JsonElement[]>("/api/jdecompare/objects?q=F5");
        Assert.Empty(list!);
    }

    public static TheoryData<object, string> InvalidCompareBodies => new()
    {
        { new { source = "", target = "PD920", objects = new[] { "F0101" } }, "Source et cible" },
        { new { source = "DV920", target = "PD920", objects = Array.Empty<string>() }, "Au moins un objet" },
        { new { source = "DV920", target = "PD920", objects = new[] { "F0101; DROP" } }, "invalide" },
        { new { source = "DV920", target = "PD920", objects = Enumerable.Range(0, 501).Select(i => $"F{i}").ToArray() }, "Trop d'objets" }
    };

    [Theory]
    [MemberData(nameof(InvalidCompareBodies))]
    public async Task JdeCompare_Compare_ValidatesInput(object body, string expected)
    {
        var res = await Client().PostAsJsonAsync("/api/jdecompare/compare", body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(expected, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task JdeCompare_Compare_UnknownEnvironment_IsRejected()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/jdecompare/compare")
        {
            Content = JsonContent.Create(new { source = "XX920", target = "PD920", objects = new[] { "F0101" } })
        };
        req.Headers.Add("X-JDE-Connection", "DV920");
        var res = await Client().SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("XX920", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task JdeCompare_ReferenceWithoutCredentials_IsRejected()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/jdecompare/statuses");
        req.Headers.Add("X-JDE-Connection", "PD920");
        var res = await Client().SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("manquants", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task JdeCompare_ObCompare_RejectsUnknownEnvironment()
    {
        var res = await Client().PostAsJsonAsync("/api/jdecompare/ob-compare", new { @object = "P0101", source = "DV920", target = "ZZ" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
