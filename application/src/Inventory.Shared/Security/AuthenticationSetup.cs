using System.Text;
using Inventory.Shared.Auditing;
using Inventory.Shared.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Inventory.Shared.Security;

public sealed class JwtSettings
{
    public string Issuer { get; set; } = "school-inventory";
    public string Audience { get; set; } = "school-inventory-api";
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 30;

    public SymmetricSecurityKey CreateKey() => new(Encoding.UTF8.GetBytes(SigningKey));

    public void Validate()
    {
        if (Encoding.UTF8.GetByteCount(SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey не задан или короче 32 байт. Задайте переменную окружения JWT_SIGNING_KEY.");
        }

        if (AccessTokenMinutes is < 1 or > 24 * 60)
        {
            throw new InvalidOperationException("Jwt:AccessTokenMinutes должен быть в диапазоне 1..1440.");
        }
    }
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddInventoryAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
        jwt.Validate();
        services.AddSingleton(jwt);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.CreateKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = InventoryClaims.Login,
                    RoleClaimType = InventoryClaims.Role
                };

                options.Events = new JwtBearerEvents
                {
                    // SR-01: обращение неавторизованного пользователя отклоняется с HTTP 403
                    // и сообщением о необходимости авторизации.
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ApiErrors.WriteAsync(
                            context.HttpContext,
                            StatusCodes.Status403Forbidden,
                            "authentication_required",
                            "Требуется авторизация. Получите токен через POST /auth/login и передайте его в заголовке Authorization: Bearer <token>.");
                    },

                    // D-01: аутентифицированный пользователь без нужной роли. Попытка
                    // фиксируется в журнале аудита как подозрительная.
                    OnForbidden = async context =>
                    {
                        var http = context.HttpContext;
                        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Inventory.AccessControl");
                        logger.LogWarning(
                            "Доступ запрещён: пользователь {Login} с ролью {Role} пытался выполнить {Method} {Path}",
                            http.User.GetLogin(), http.User.GetRole(), http.Request.Method, http.Request.Path);

                        var audit = http.RequestServices.GetService<IAuditRecorder>();
                        if (audit is not null)
                        {
                            try
                            {
                                await audit.RecordNowAsync(new AuditEntry(
                                    Action: "access.denied",
                                    Result: AuditResults.Denied,
                                    TargetType: "endpoint",
                                    TargetId: $"{http.Request.Method} {http.Request.Path}",
                                    Details: "Роль пользователя не имеет права на эту операцию",
                                    Severity: AuditSeverity.Warning,
                                    Flags: ["role_violation"]), http.RequestAborted);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Не удалось записать событие access.denied в журнал аудита");
                            }
                        }

                        await ApiErrors.WriteAsync(
                            http,
                            StatusCodes.Status403Forbidden,
                            "forbidden",
                            "Операция недоступна для вашей роли.");
                    }
                };
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
            .AddPolicy(Policies.Teacher, p => p.RequireAuthenticatedUser().RequireRole(Roles.Teacher))
            .AddPolicy(Policies.Student, p => p.RequireAuthenticatedUser().RequireRole(Roles.Student))
            .AddPolicy(Policies.AdminOrTeacher, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.Teacher))
            .AddPolicy(Policies.TeacherOrStudent, p => p.RequireAuthenticatedUser().RequireRole(Roles.Teacher, Roles.Student))
            .AddPolicy(Policies.AnyRole, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.Teacher, Roles.Student));

        return services;
    }
}
