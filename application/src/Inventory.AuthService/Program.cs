using Inventory.AuthService.Data;
using Inventory.AuthService.Endpoints;
using Inventory.AuthService.Services;
using Inventory.Shared.Hosting;
using Inventory.Shared.Http;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("auth-service");
builder.AddServiceDatabase<AuthDbContext>();

builder.Services.Configure<LoginProtectionOptions>(builder.Configuration.GetSection("LoginProtection"));
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<TokenService>();

builder.Services
    .AddHttpClient<DeviceHoldingsClient>(c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:DeviceService"] ?? "http://device-service:8080/");
        c.Timeout = TimeSpan.FromSeconds(10);
    })
    .AddHttpMessageHandler<ForwardBearerTokenHandler>();

var app = builder.Build();

app.UseServiceDefaults();
app.MapAuthEndpoints();
app.MapUserEndpoints();

await app.InitializeDatabaseAsync<AuthDbContext>(AuthSeeder.SeedAsync);
await app.RunAsync();
