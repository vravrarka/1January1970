using Inventory.Shared.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Inventory.AuthService.Data;

public static class AuthSeeder
{
    /// <summary>
    /// Создаёт первого администратора из конфигурации и, если включено, демонстрационных
    /// пользователей. Без администратора управлять системой через API невозможно.
    /// </summary>
    public static async Task SeedAsync(AuthDbContext db, IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var hasher = services.GetRequiredService<IPasswordHasher<User>>();
        var time = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AuthSeeder));

        if (!await db.Users.AnyAsync(u => u.Role == Roles.Admin))
        {
            var login = (configuration["Admin:Login"] ?? "admin").Trim().ToLowerInvariant();
            var password = configuration["Admin:Password"];
            if (string.IsNullOrEmpty(password) || password.Length < 8)
            {
                throw new InvalidOperationException(
                    "В системе нет администратора. Задайте ADMIN_PASSWORD (не короче 8 символов) для первичной инициализации.");
            }

            Add(db, hasher, time, login, configuration["Admin:FullName"] ?? "Системный администратор", Roles.Admin, password);
            logger.LogInformation("Создан администратор {Login}", login);
        }

        if (configuration.GetValue<bool>("Seed:DemoUsers"))
        {
            var demoPassword = configuration["Seed:DemoPassword"];
            if (string.IsNullOrEmpty(demoPassword) || demoPassword.Length < 8)
            {
                throw new InvalidOperationException("Seed:DemoUsers включён, но DEMO_PASSWORD не задан или короче 8 символов.");
            }

            (string Login, string Name, string Role)[] demo =
            [
                ("teacher01", "Мария Петровна Смирнова", Roles.Teacher),
                ("student01", "Иван Иванов", Roles.Student),
                ("student02", "Анна Кузнецова", Roles.Student)
            ];

            foreach (var (login, name, role) in demo)
            {
                if (!await db.Users.AnyAsync(u => u.Login == login))
                {
                    Add(db, hasher, time, login, name, role, demoPassword);
                    logger.LogInformation("Создан демонстрационный пользователь {Login} ({Role})", login, role);
                }
            }
        }

        await db.SaveChangesAsync();
    }

    private static void Add(AuthDbContext db, IPasswordHasher<User> hasher, TimeProvider time,
        string login, string name, string role, string password)
    {
        var user = new User { Login = login, FullName = name, Role = role, CreatedAt = time.GetUtcNow() };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
    }
}
