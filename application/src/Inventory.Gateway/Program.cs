using System.Threading.RateLimiting;
using Inventory.Gateway;

var builder = WebApplication.CreateBuilder(args);

var httpPort = builder.Configuration.GetValue("Gateway:HttpPort", 8080);
var httpsPort = builder.Configuration.GetValue("Gateway:HttpsPort", 8443);
var publicHttpsPort = builder.Configuration.GetValue("Gateway:PublicHttpsPort", httpsPort);
var certificate = GatewayCertificate.LoadOrCreate(builder.Configuration);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 1024 * 1024;

    // HTTP-порт существует только для перенаправления на HTTPS (SR-04).
    kestrel.ListenAnyIP(httpPort);
    kestrel.ListenAnyIP(httpsPort, listen => listen.UseHttps(certificate));
});

builder.Services.AddHttpsRedirection(o =>
{
    o.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
    o.HttpsPort = publicHttpsPort;
});
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});

// Ограничение частоты попыток входа с одного адреса (защита от перебора паролей).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Gateway:LoginPermitPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseHsts();
app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapReverseProxy();

app.Run();
