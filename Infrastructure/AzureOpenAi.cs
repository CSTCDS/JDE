using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Utils.Colas.Infrastructure;

public sealed class AzureOpenAiOptions
{
    // https://<ressource>.openai.azure.com
    public string Endpoint { get; set; } = string.Empty;
    public string Deployment { get; set; } = string.Empty;
    // Dans secrets.json (en clair ou ENC: via Tools\encrypt-secrets.ps1).
    public string ApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2024-10-21";
    public int TimeoutSeconds { get; set; } = 90;
    // Taille maximale du diff envoyé (caractères).
    public int MaxInputChars { get; set; } = 30000;
    // Proxy sortant éventuel (ex. http://proxy:8080), authentifié avec l'identité du pool IIS.
    public string Proxy { get; set; } = string.Empty;
}

public sealed class AiException(string message, int statusCode = StatusCodes.Status502BadGateway) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class AzureOpenAiClient
{
    public const string HttpClientName = "AzureOpenAI";

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<AzureOpenAiClient> _logger;
    private readonly AzureOpenAiOptions _opt;

    public AzureOpenAiClient(IHttpClientFactory factory, IConfiguration configuration, ConnectionStore store, ILogger<AzureOpenAiClient> logger)
    {
        _factory = factory;
        _logger = logger;
        _opt = Bind(configuration);
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            _opt.ApiKey = store.Unprotect(_opt.ApiKey, "AzureOpenAI:ApiKey");
        if (Enabled)
            logger.LogInformation("Analyse IA : Azure OpenAI {Endpoint}, déploiement {Deployment}.", _opt.Endpoint, _opt.Deployment);
        else
            logger.LogInformation("Analyse IA désactivée (AzureOpenAI : Endpoint, Deployment ou ApiKey non renseigné).");
    }

    public static AzureOpenAiOptions Bind(IConfiguration configuration)
    {
        var opt = new AzureOpenAiOptions();
        configuration.GetSection("AzureOpenAI").Bind(opt);
        return opt;
    }

    public AzureOpenAiOptions Options => _opt;

    public bool Enabled => !string.IsNullOrWhiteSpace(_opt.Endpoint) && !string.IsNullOrWhiteSpace(_opt.Deployment) && !string.IsNullOrWhiteSpace(_opt.ApiKey);

    public async Task<string> ChatAsync(string system, string user, CancellationToken ct)
    {
        if (!Enabled) throw new AiException("Analyse IA non configurée (section AzureOpenAI).", StatusCodes.Status503ServiceUnavailable);

        var url = $"{_opt.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(_opt.Deployment)}/chat/completions?api-version={Uri.EscapeDataString(_opt.ApiVersion)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new
            {
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                }
            })
        };
        req.Headers.Add("api-key", _opt.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));
        HttpResponseMessage res;
        try
        {
            res = await _factory.CreateClient(HttpClientName).SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"Azure OpenAI n'a pas répondu en {_opt.TimeoutSeconds} s.", StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Azure OpenAI injoignable ({Endpoint})", _opt.Endpoint);
            throw new AiException($"Azure OpenAI injoignable depuis le serveur ({ex.Message}). Vérifier Endpoint, proxy (AzureOpenAI:Proxy) et pare-feu.");
        }

        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError("Azure OpenAI {Status} : {Body}", (int)res.StatusCode, body.Length > 2000 ? body[..2000] : body);
                throw new AiException(res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Azure OpenAI : clé refusée (AzureOpenAI:ApiKey).",
                    HttpStatusCode.NotFound => $"Azure OpenAI : déploiement '{_opt.Deployment}' ou version d'API '{_opt.ApiVersion}' introuvable.",
                    HttpStatusCode.TooManyRequests => "Azure OpenAI : quota dépassé, réessayer dans un instant.",
                    HttpStatusCode.BadRequest when body.Contains("content_filter", StringComparison.OrdinalIgnoreCase) => "Azure OpenAI : contenu bloqué par le filtre de contenu.",
                    _ => $"Azure OpenAI : erreur HTTP {(int)res.StatusCode}."
                });
            }
            try
            {
                using var doc = JsonDocument.Parse(body);
                var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return string.IsNullOrWhiteSpace(content) ? throw new AiException("Azure OpenAI : réponse vide.") : content.Trim();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                throw new AiException("Azure OpenAI : réponse inattendue.");
            }
        }
    }
}
