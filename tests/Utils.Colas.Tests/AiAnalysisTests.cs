using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Utils.Colas.Infrastructure;
using Utils.Colas.Modules.JdeCompare;

namespace Utils.Colas.Tests;

public class AiAnalysisTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static AzureOpenAiClient Client(HttpMessageHandler handler, bool configured = true)
    {
        var values = new Dictionary<string, string?>();
        if (configured)
        {
            values["AzureOpenAI:Endpoint"] = "https://res.openai.azure.com/";
            values["AzureOpenAI:Deployment"] = "gpt-4o";
            values["AzureOpenAI:ApiKey"] = "cle-secrete";
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var store = new ConnectionStore(config, new EphemeralDataProtectionProvider(), NullLogger<ConnectionStore>.Instance);
        return new AzureOpenAiClient(new StubFactory(handler), config, store, NullLogger<AzureOpenAiClient>.Instance);
    }

    [Fact]
    public async Task Chat_SendsDeploymentUrlAndKey_AndReturnsContent()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"  Synthèse : ajout d'un champ.  "}}]}""");
        var result = await Client(handler).ChatAsync("sys", "diff", CancellationToken.None);

        Assert.Equal("Synthèse : ajout d'un champ.", result);
        Assert.Equal("https://res.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21", handler.Request!.RequestUri!.ToString());
        Assert.Equal("cle-secrete", handler.Request.Headers.GetValues("api-key").Single());
        Assert.Contains("\"role\":\"system\"", handler.RequestBody);
        Assert.Contains("\"content\":\"diff\"", handler.RequestBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "clé refusée")]
    [InlineData(HttpStatusCode.NotFound, "introuvable")]
    [InlineData(HttpStatusCode.TooManyRequests, "quota")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task Chat_MapsHttpErrors(HttpStatusCode status, string expected)
    {
        var ex = await Assert.ThrowsAsync<AiException>(() => Client(new StubHandler(status, "{}")).ChatAsync("s", "u", CancellationToken.None));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Chat_UnexpectedPayload_Throws()
    {
        var ex = await Assert.ThrowsAsync<AiException>(() => Client(new StubHandler(HttpStatusCode.OK, """{"foo":1}""")).ChatAsync("s", "u", CancellationToken.None));
        Assert.Contains("inattendue", ex.Message);
    }

    [Fact]
    public async Task Chat_NotConfigured_Is503()
    {
        var client = Client(new StubHandler(HttpStatusCode.OK, "{}"), configured: false);
        Assert.False(client.Enabled);
        var ex = await Assert.ThrowsAsync<AiException>(() => client.ChatAsync("s", "u", CancellationToken.None));
        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public void ScreenLines_CountsModifiedTwice()
    {
        var diff = LineDiff.Generate("a\nb\nc", "a\nB\nc\nd");
        Assert.Equal(3, LineDiff.ScreenLines(diff));
        Assert.Equal(0, LineDiff.ScreenLines(LineDiff.Generate("x", "x")));
    }

    [Fact]
    public void ToText_FormatsAndTruncates()
    {
        var diff = LineDiff.Generate("a\nb", "a\nB\nc");
        Assert.Equal("- L2: b\n+ L2: B\n+ L3: c\n", LineDiff.ToText(diff, 1000));
        Assert.EndsWith("(diff tronqué pour l'analyse)\n", LineDiff.ToText(diff, 10));
    }

    [Fact]
    public void Prompt_DescribesObjectFileAndEnvironments()
    {
        var src = new PackageFile("source/F55X.c", Encoding.UTF8.GetBytes("a\nb"));
        var d = new FileDiff("F55X", "DV920", "PD920", "source/F55X.c", src, null, false, "a\nb", "", LineDiff.Generate("a\nb", ""));
        var prompt = AiPrompt.User(d, 1000);
        Assert.Contains("Objet JDE : F55X", prompt);
        Assert.Contains("Fichier : source/F55X.c", prompt);
        Assert.Contains("Source : DV920 (2 lignes)", prompt);
        Assert.Contains("Cible : PD920 (fichier absent)", prompt);
        Assert.Contains("- L1: a", prompt);

        var chat = AiPrompt.ForChat(d, 1000);
        Assert.StartsWith(AiPrompt.System, chat);
        Assert.EndsWith(prompt, chat);
    }
}
