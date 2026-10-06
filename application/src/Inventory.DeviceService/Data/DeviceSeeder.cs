using Inventory.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.DeviceService.Data;

public static class DeviceSeeder
{
    public static async Task SeedAsync(DeviceDbContext db, IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue<bool>("Seed:DemoDevices") || await db.Devices.AnyAsync())
        {
            return;
        }

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        (string Name, DeviceType Type, string Description)[] demo =
        [
            ("MacBook Air M2", DeviceType.Laptop, "Инв. № IT-0001"),
            ("Lenovo ThinkPad E14", DeviceType.Laptop, "Инв. № IT-0002"),
            ("iPad 10", DeviceType.Tablet, "Инв. № IT-0101"),
            ("Samsung Galaxy Tab S9", DeviceType.Tablet, "Инв. № IT-0102"),
            ("Arduino Uno R4", DeviceType.Microcontroller, "Инв. № IT-0201"),
            ("Raspberry Pi 5", DeviceType.Microcontroller, "Инв. № IT-0202"),
            ("Meta Quest 3", DeviceType.VrHeadset, "Инв. № IT-0301"),
            ("Sony ZV-E10", DeviceType.Camera, "Инв. № IT-0401")
        ];

        foreach (var (name, type, description) in demo)
        {
            db.Devices.Add(new Device
            {
                Name = name,
                Type = type,
                Description = description,
                Status = DeviceStatus.Available,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync();
    }
}
