using Inventory.Shared.Auditing;
using Inventory.Shared.Http;
using Inventory.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inventory.Shared.Hosting;

public static class ServiceDefaults
{
    /// <summary>
    /// Общая конфигурация микросервиса: JSON, проблемы, заголовки прокси,
    /// JWT-аутентификация и политики ролей, рабочее время для аудита.
    /// </summary>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));

        // Сервисы доступны только во внутренней сети Docker, единственный источник
        // запросов — шлюз, поэтому доверяем его заголовкам X-Forwarded-*.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
        });

        services.AddHealthChecks();
        services.AddHttpContextAccessor();
        services.AddSingleton(TimeProvider.System);

        services.Configure<WorkingHoursOptions>(configuration.GetSection("WorkingHours"));
        services.AddSingleton<WorkingHoursPolicy>();

        var kafka = configuration.GetSection("Kafka").Get<KafkaOptions>() ?? new KafkaOptions();
        services.AddSingleton(kafka);
        services.Configure<AuditOptions>(o =>
        {
            o.ServiceName = serviceName;
            o.Topic = kafka.AuditTopic;
        });

        services.AddInventoryAuthentication(configuration);
        services.AddTransient<ForwardBearerTokenHandler>();

        return builder;
    }

    /// <summary>
    /// Регистрирует DbContext сервиса (PostgreSQL, имена в snake_case) и журнал аудита на его основе.
    /// </summary>
    public static WebApplicationBuilder AddServiceDatabase<TContext>(this WebApplicationBuilder builder)
        where TContext : DbContext, IOutboxStore
    {
        var connectionString = builder.Configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Default.");

        builder.Services.AddDbContext<TContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        builder.Services.AddScoped<IAuditRecorder, AuditRecorder<TContext>>();

        var kafka = builder.Configuration.GetSection("Kafka").Get<KafkaOptions>() ?? new KafkaOptions();
        if (kafka.Enabled)
        {
            builder.Services.AddHostedService<OutboxPublisher<TContext>>();
        }

        return builder;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health").AllowAnonymous();
        return app;
    }

    /// <summary>
    /// Создаёт схему БД при первом запуске. PostgreSQL в Docker может стартовать позже
    /// сервиса, поэтому подключение повторяется.
    /// </summary>
    public static async Task InitializeDatabaseAsync<TContext>(
        this WebApplication app,
        Func<TContext, IServiceProvider, Task>? seed = null,
        int maxAttempts = 30)
        where TContext : DbContext
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();
                await db.Database.EnsureCreatedAsync();
                if (seed is not null)
                {
                    await seed(db, scope.ServiceProvider);
                }

                logger.LogInformation("База данных {Context} готова", typeof(TContext).Name);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning("БД недоступна (попытка {Attempt}/{Max}): {Message}", attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
