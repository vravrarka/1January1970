using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Inventory.AuthService.Data;
using Inventory.AuthService.Services;
using Inventory.Shared.Auditing;
using Inventory.Shared.Http;
using Inventory.Shared.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inventory.AuthService.Endpoints;

public sealed record UserDto(Guid Id, string Login, string Name, string Role, DateTimeOffset CreatedAt)
{
    public static UserDto From(User u) => new(u.Id, u.Login, u.FullName, u.Role, u.CreatedAt);
}

public sealed record CreateUserRequest(string? Name, string? Login, string? Role, string? Password, string? Confirmation);

public sealed record CreatedUserResponse(Guid Id, string Login, string Name, string Role, DateTimeOffset CreatedAt, string? TemporaryPassword);

public static partial class UserEndpoints
{
    /// <summary>SR-05: создание и удаление аккаунта требует подтверждения фразой «Подтверждаю».</summary>
    public const string ConfirmationPhrase = "Подтверждаю";

    [GeneratedRegex("^[a-z0-9._-]{3,32}$")]
    private static partial Regex LoginPattern();

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users").WithTags("Users");

        group.MapGet("/", ListAsync).RequireAuthorization(Policies.AdminOrTeacher);
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Admin);
        group.MapDelete("/{userId:guid}", DeleteAsync).RequireAuthorization(Policies.Admin);

        return app;
    }

    private static async Task<IResult> ListAsync(string? role, HttpContext http, AuthDbContext db, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();

        // Учётные записи администраторов видит только администратор.
        if (http.User.GetRole() != Roles.Admin)
        {
            query = query.Where(u => u.Role != Roles.Admin);
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var normalized = role.Trim().ToLowerInvariant();
            if (!Roles.All.Contains(normalized))
            {
                return ApiErrors.BadRequest("invalid_role", "Допустимые значения role: student, teacher, admin.");
            }

            query = query.Where(u => u.Role == normalized);
        }

        var users = await query.OrderBy(u => u.Role).ThenBy(u => u.FullName).ToListAsync(ct);
        return Results.Ok(users.Select(UserDto.From));
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        AuthDbContext db,
        IPasswordHasher<User> hasher,
        IAuditRecorder audit,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request.Confirmation?.Trim() != ConfirmationPhrase)
        {
            return ApiErrors.BadRequest("confirmation_required",
                $"Для создания аккаунта передайте поле confirmation со значением «{ConfirmationPhrase}».");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return ApiErrors.BadRequest("invalid_name", "Поле name обязательно, не длиннее 200 символов.");
        }

        var login = request.Login?.Trim().ToLowerInvariant();
        if (login is null || !LoginPattern().IsMatch(login))
        {
            return ApiErrors.BadRequest("invalid_login",
                "Поле login: 3–32 символа, латинские буквы в нижнем регистре, цифры, точка, дефис, подчёркивание.");
        }

        // Роль создаваемого пользователя приходит из запроса, но это лишь данные операции.
        // Права текущего пользователя определяются из токена (threat-model.md, раздел 8).
        var role = request.Role?.Trim().ToLowerInvariant();
        if (role is null || !Roles.Assignable.Contains(role))
        {
            return ApiErrors.BadRequest("invalid_role", "Поле role может принимать значения student или teacher.");
        }

        string? temporaryPassword = null;
        var password = request.Password;
        if (string.IsNullOrEmpty(password))
        {
            temporaryPassword = password = GeneratePassword();
        }
        else if (password.Length is < 8 or > 128)
        {
            return ApiErrors.BadRequest("invalid_password", "Пароль должен содержать от 8 до 128 символов.");
        }

        if (await db.Users.AnyAsync(u => u.Login == login, ct))
        {
            return ApiErrors.Conflict("login_taken", $"Логин «{login}» уже занят.");
        }

        var user = new User
        {
            Login = login,
            FullName = name,
            Role = role,
            CreatedAt = time.GetUtcNow()
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);

        audit.Record(new AuditEntry("user.created", AuditResults.Success, "user", user.Id.ToString(),
            Details: $"Создан пользователь {login} с ролью {role}"));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiErrors.Conflict("login_taken", $"Логин «{login}» уже занят.");
        }

        return Results.Created($"/users/{user.Id}",
            new CreatedUserResponse(user.Id, user.Login, user.FullName, user.Role, user.CreatedAt, temporaryPassword));
    }

    private static async Task<IResult> DeleteAsync(
        Guid userId,
        string? confirmation,
        HttpContext http,
        AuthDbContext db,
        DeviceHoldingsClient holdings,
        IAuditRecorder audit,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (confirmation?.Trim() != ConfirmationPhrase)
        {
            return ApiErrors.BadRequest("confirmation_required",
                $"Для удаления аккаунта передайте параметр запроса confirmation={ConfirmationPhrase}.");
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return ApiErrors.NotFound("user_not_found", "Пользователь не найден.");
        }

        if (!Roles.Assignable.Contains(user.Role) || user.Id == http.User.GetUserId())
        {
            return ApiErrors.Forbidden("cannot_delete_admin", "Через API можно удалять только учеников и учителей.");
        }

        if (user.Role == Roles.Student)
        {
            int held;
            try
            {
                held = await holdings.CountHeldDevicesAsync(user.Id, ct);
            }
            catch (HttpRequestException ex)
            {
                loggerFactory.CreateLogger("Users").LogError(ex, "Сервис устройств недоступен");
                return ApiErrors.Unavailable("device_service_unavailable",
                    "Не удалось проверить выданные ученику устройства. Повторите позже.");
            }

            if (held > 0)
            {
                return ApiErrors.Conflict("student_has_devices",
                    $"У ученика {held} невозвращённых устройств. Сначала оформите возврат.");
            }
        }

        db.Users.Remove(user);
        audit.Record(new AuditEntry("user.deleted", AuditResults.Success, "user", user.Id.ToString(),
            Details: $"Удалён пользователь {user.Login} с ролью {user.Role}"));
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 14);
    }
}
