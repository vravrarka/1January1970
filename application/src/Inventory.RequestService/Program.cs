using Inventory.RequestService.Data;
using Inventory.RequestService.Endpoints;
using Inventory.RequestService.Services;
using Inventory.Shared.Hosting;
using Inventory.Shared.Http;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("request-service");
builder.AddServiceDatabase<RequestDbContext>();

builder.Services
    .AddHttpClient<DeviceServiceClient>(c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:DeviceService"] ?? "http://device-service:8080/");
        c.Timeout = TimeSpan.FromSeconds(10);
    })
    .AddHttpMessageHandler<ForwardBearerTokenHandler>();

var app = builder.Build();

app.UseServiceDefaults();
app.MapRequestEndpoints();

await app.InitializeDatabaseAsync<RequestDbContext>();
await app.RunAsync();
