using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inventory.Shared.Auditing;

public sealed class KafkaOptions
{
    /// <summary>При <c>false</c> события остаются только в локальном журнале сервиса.</summary>
    public bool Enabled { get; set; } = true;

    public string BootstrapServers { get; set; } = "kafka:9092";
    public string AuditTopic { get; set; } = "audit-events";
    public string ConsumerGroup { get; set; } = "audit-service";
    public int PublishIntervalSeconds { get; set; } = 2;
    public int RetryDelaySeconds { get; set; } = 10;
}

/// <summary>
/// Фоновая отправка событий из локального журнала в Kafka. Пока Kafka недоступна,
/// события накапливаются в таблице audit_outbox и отправляются после восстановления
/// связи в исходном порядке (SR-03).
/// </summary>
public sealed class OutboxPublisher<TContext>(
    IServiceScopeFactory scopeFactory,
    KafkaOptions kafka,
    TimeProvider time,
    ILogger<OutboxPublisher<TContext>> logger) : BackgroundService
    where TContext : DbContext, IOutboxStore
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 5000,
            SocketTimeoutMs = 5000,
            ClientId = typeof(TContext).Name
        };

        using var producer = new ProducerBuilder<string, string>(config)
            .SetLogHandler((_, message) => logger.LogDebug("Kafka: {Message}", message.Message))
            .SetErrorHandler((_, error) => logger.LogDebug("Kafka error: {Reason}", error.Reason))
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(producer, stoppingToken);
                if (published < BatchSize)
                {
                    await Task.Delay(TimeSpan.FromSeconds(kafka.PublishIntervalSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Не удалось отправить события аудита в Kafka ({Reason}). События сохранены локально, повтор через {Delay} с.",
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

    private async Task<int> PublishBatchAsync(IProducer<string, string> producer, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        var batch = await db.AuditOutbox
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var message in batch)
        {
            try
            {
                await producer.ProduceAsync(
                    message.Topic,
                    new Message<string, string> { Key = message.EventId.ToString(), Value = message.Payload },
                    cancellationToken);

                message.PublishedAt = time.GetUtcNow();
                message.LastError = null;
                published++;
            }
            catch (ProduceException<string, string> ex)
            {
                message.Attempts++;
                message.LastError = Truncate(ex.Error.Reason, 1000);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }

        if (batch.Count > 0)
        {
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Отправлено в Kafka событий аудита: {Count}", published);
        }

        return published;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
