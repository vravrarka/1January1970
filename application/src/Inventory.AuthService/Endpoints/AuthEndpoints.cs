using Inventory.AuthService.Data;
using Inventory.AuthService.Services;
using Inventory.Shared.Auditing;
using Inventory.Shared.Http;
using Inventory.Shared.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.AuthService.Endpoints;

public sealed class LoginProtectionOptions
{
    /// <summary>После стольких неудачных попыток подряд событие помечается предупреждением.</summary>
    public int WarningThreshold { get; set; } = 3;

    /// <summary>После стольких неудачных попыток подряд учётная запись временно блокируется.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;
}

public sealed record LoginRequest(string? Login, string? Password);

public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, UserDto User);

public static class AuthEndpoints
{
    private const string InvalidCredentials = "Неверный логин или пароль.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization(Policies.AnyRole);
        group.MapGet("/me", (HttpContext http) => Results.Ok(new
        {
            id = http.User.GetUserId(),
            login = http.User.GetLogin(),
            name = http.User.GetFullName(),
            role = http.User.GetRole()
        })).RequireAuthorization(Policies.AnyRole);

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AuthDbContext db,
        IPasswordHasher<User> hasher,
        TokenService tokens,
        IAuditRecorder audit,
        TimeProvider time,
        IOptions<LoginProtectionOptions> protectionOptions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrEmpty(request.Password) ||
            request.Login.Length > 64 || request.Password.Length > 256)
        {
            return ApiErrors.BadRequest("invalid_input", "Укажите login и password.");
        }

        var protection = protectionOptions.Value;
        var login = request.Login.Trim().ToLowerInvariant();
        var now = time.GetUtcNow();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Login == login, ct);

        if (user is null)
        {
            // Выравниваем время ответа, чтобы по нему нельзя было перебирать существующие логины.
            hasher.VerifyHashedPassword(DummyUser.Instance, DummyUser.Hash, request.Password);
            await audit.RecordNowAsync(new AuditEntry(
                "auth.login", AuditResults.Failed, "user", login,
                Details: "Неизвестный логин",
                Actor: new AuditActor(null, login, null)), ct);
            return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "invalid_credentials", InvalidCredentials);
        }

        var actor = new AuditActor(user.Id, user.Login, user.Role);

        if (user.LockoutUntil is { } until && until > now)
        {
            await audit.RecordNowAsync(new AuditEntry(
                "auth.login", AuditResults.Denied, "user", user.Id.ToString(),
                Details: $"Учётная запись заблокирована до {until:O}",
                Severity: AuditSeverity.Warning,
                Flags: [AuditFlags.AccountLocked],
                Actor: actor), ct);
            return ApiErrors.Problem(StatusCodes.Status429TooManyRequests, "account_locked",
                "Слишком много неудачных попыток входа. Повторите позже.");
        }

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            var flags = new List<string>();
            var severity = AuditSeverity.Info;
            var details = $"Неверный пароль, неудачных попыток подряд: {user.FailedLoginCount}";

            if (user.FailedLoginCount >= protection.WarningThreshold)
            {
                flags.Add(AuditFlags.MultipleFailedLogins);
                severity = AuditSeverity.Warning;
            }

            if (user.FailedLoginCount >= protection.MaxFailedAttempts)
            {
                user.LockoutUntil = now.AddMinutes(protection.LockoutMinutes);
                user.FailedLoginCount = 0;
                flags.Add(AuditFlags.AccountLocked);
                details += $". Учётная запись заблокирована на {protection.LockoutMinutes} мин.";
            }

            audit.Record(new AuditEntry(
                "auth.login", AuditResults.Failed, "user", user.Id.ToString(),
                details, severity, flags, actor));
            await db.SaveChangesAsync(ct);
            return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "invalid_credentials", InvalidCredentials);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;

        var token = tokens.Issue(user);
        audit.Record(new AuditEntry("auth.login", AuditResults.Success, "user", user.Id.ToString(), Actor: actor));
        await db.SaveChangesAsync(ct);

        return Results.Ok(new LoginResponse(token.AccessToken, "Bearer", token.ExpiresAt, UserDto.From(user)));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, IAuditRecorder audit, CancellationToken ct)
    {
        await audit.RecordNowAsync(new AuditEntry(
            "auth.logout", AuditResults.Success, "user", http.User.GetUserId().ToString()), ct);
        return Results.NoContent();
    }

    private static class DummyUser
    {
        public static readonly User Instance = new() { Login = "dummy" };
        public static readonly string Hash = new PasswordHasher<User>().HashPassword(Instance, Guid.NewGuid().ToString());
    }
}
