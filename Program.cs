using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.ResponseCompression;
using Utils.Colas.Infrastructure;
using Utils.Colas.Modules.JdeCompare;
using Utils.Colas.Modules.LireH;

var builder = WebApplication.CreateBuilder(args);

// Identifiants Oracle hors Git : secrets.json (DPAPI) à côté de l'application.
builder.Configuration.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "secrets.json"), optional: true, reloadOnChange: false);
// Option : fichier de connexions partagé (ex. appsettings.json racine JDE).
if (builder.Configuration["ConnectionsFile"] is { Length: > 0 } sharedFile)
    builder.Configuration.AddJsonFile(sharedFile, optional: false, reloadOnChange: false);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1_000_000);
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

builder.Services.AddDataProtection()
    .SetApplicationName("Utils.Colas")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")));
builder.Services.AddSingleton<ConnectionStore>();

// Analyse IA (JDE Compare) : Azure OpenAI, proxy sortant optionnel.
builder.Services.AddHttpClient(AzureOpenAiClient.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var proxy = AzureOpenAiClient.Bind(builder.Configuration).Proxy;
        return string.IsNullOrWhiteSpace(proxy)
            ? new SocketsHttpHandler()
            : new SocketsHttpHandler { Proxy = new System.Net.WebProxy(proxy) { UseDefaultCredentials = true }, UseProxy = true };
    });
builder.Services.AddSingleton<AzureOpenAiClient>();

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonyme",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Fail fast : déchiffrement des connexions et validation de la configuration des modules au démarrage.
app.Services.GetRequiredService<ConnectionStore>();
app.Services.GetRequiredService<AzureOpenAiClient>();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();

app.Use(async (ctx, next) =>
{
    var correlationId = ctx.Request.Headers["X-Correlation-ID"].FirstOrDefault() is { Length: > 0 and <= 64 } id ? id : ctx.TraceIdentifier;
    var h = ctx.Response.Headers;
    h["X-Correlation-ID"] = correlationId;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        await next();
});

app.UseResponseCompression();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Pages : no-store ; JS/CSS/config non hashés : no-cache (revalidation, jamais de Ctrl+F5).
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var isHtml = ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        ctx.Context.Response.Headers.CacheControl = isHtml ? "no-store, no-cache, must-revalidate" : "no-cache";
        if (isHtml) ctx.Context.Response.Headers.Pragma = "no-cache";
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().DisableRateLimiting();

app.MapGet("/api/version", () =>
{
    var asm = Assembly.GetExecutingAssembly();
    var version = (asm.GetName().Version ?? new Version(1, 0, 0)).ToString(3);
    var built = File.GetLastWriteTime(asm.Location);
    // Identifiant de livraison (UPDyyyyMMdd-HHmm) injecté par build-update.ps1 ; sinon « local ».
    var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
    var release = info.StartsWith("UPD", StringComparison.Ordinal) ? info.Split('+')[0] : "local";
    return Results.Ok(new { version, release, built = built.ToString("yyyy-MM-dd HH:mm:ss") });
});

app.MapGet("/api/windows-user", (HttpContext ctx) =>
{
    var name = ctx.User.Identity?.Name ?? string.Empty;
    return Results.Ok(new { user = name.Contains('\\') ? name.Split('\\').Last() : name });
});

// Connexions de référence (bandeau commun) — sans identifiants.
app.MapGet("/api/connections", (ConnectionStore store, IConfiguration cfg) => Results.Ok(new
{
    defaultConnection = cfg["DefaultConnection"],
    connections = store.Ordered.Select(c => new { name = c.Name, environment = c.Environment, bannerColor = c.BannerColor, available = c.HasCredentials })
}));

app.MapLireH();
app.MapJdeCompare();

app.Run();

public partial class Program;
