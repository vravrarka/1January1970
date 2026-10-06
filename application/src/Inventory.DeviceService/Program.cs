using Inventory.DeviceService.Data;
using Inventory.DeviceService.Endpoints;
using Inventory.Shared.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("device-service");
builder.AddServiceDatabase<DeviceDbContext>();

var app = builder.Build();

app.UseServiceDefaults();
app.MapDeviceEndpoints();
app.MapInternalEndpoints();

await app.InitializeDatabaseAsync<DeviceDbContext>(DeviceSeeder.SeedAsync);
await app.RunAsync();
