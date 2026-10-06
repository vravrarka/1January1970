using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Inventory.AuditService.Data;
using Inventory.Shared.Auditing;
using Inventory.Shared.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inventory.AuditService.Services;

/// <summary>
/// Читает события аудита из Kafka и сохраняет их в централизованный журнал.
/// Смещение фиксируется только после записи в БД, а повторная доставка того же события
/// игнорируется по его идентификатору. Так события не теряются и не дублируются.
/// </summary>
public sealed class AuditConsumer(
    IServiceScopeFactory scopeFactory,
    KafkaOptions kafka,
    TimeProvider time,
    ILogger<AuditConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = kafka.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = true
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureTopicAsync();

                using var consumer = new ConsumerBuilder<string, string>(config)
                    .SetErrorHandler((_, e) => logger.LogDebug("Kafka error: {Reason}", e.Reason))
                    .Build();
                consumer.Subscribe(kafka.AuditTopic);
                logger.LogInformation("Подписка на топик {Topic} оформлена", kafka.AuditTopic);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var result = consumer.Consume(stoppingToken);
                    if (result?.Message?.Value is null)
                    {
                        continue;
                    }

                    await StoreAsync(result.Message.Value, stoppingToken);
                    consumer.Commit(result);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Kafka недоступна или ошибка обработки ({Message}). Повтор через {Delay} с.",
                    ex.Message, kafka.RetryDelaySeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(kafka.RetryDelaySeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task EnsureTopicAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(
                [new TopicSpecification { Name = kafka.AuditTopic, NumPartitions = 1, ReplicationFactor = 1 }],
                new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
            logger.LogInformation("Создан топик {Topic}", kafka.AuditTopic);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Топик уже существует.
        }
    }

    private async Task StoreAsync(string payload, CancellationToken ct)
    {
        AuditEvent? auditEvent;
        try
        {
            auditEvent = JsonSerializer.Deserialize<AuditEvent>(payload, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Некорректное событие аудита пропущено");
            return;
        }

        if (auditEvent is null || auditEvent.Id == Guid.Empty || string.IsNullOrEmpty(auditEvent.Action))
        {
            logger.LogError("Событие аудита без идентификатора или действия пропущено");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        if (await db.AuditRecords.AnyAsync(r => r.Id == auditEvent.Id, ct))
        {
            return;
        }

        db.AuditRecords.Add(AuditRecord.From(auditEvent, time.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Событие уже сохранено параллельно.
        }

        if (auditEvent.Severity == AuditSeverity.Warning)
        {
            logger.LogWarning("ПРЕДУПРЕЖДЕНИЕ аудита: {Action} от {Actor} ({Flags}): {Details}",
                auditEvent.Action, auditEvent.ActorLogin ?? "аноним", string.Join(",", auditEvent.Flags), auditEvent.Details);
        }
    }
}
