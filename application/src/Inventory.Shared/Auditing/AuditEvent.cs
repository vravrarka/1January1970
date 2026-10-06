namespace Inventory.Shared.Auditing;

/// <summary>
/// Событие журнала аудита. Отвечает на вопросы из threat-model.md (раздел «Журнал аудита»):
/// кто выполнил действие, какое, над каким объектом, когда и с каким результатом.
/// </summary>
public sealed record AuditEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; }
    public string Service { get; init; } = string.Empty;

    public Guid? ActorId { get; init; }
    public string? ActorLogin { get; init; }
    public string? ActorRole { get; init; }

    public string Action { get; init; } = string.Empty;
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string Result { get; init; } = AuditResults.Success;

    public string Severity { get; init; } = AuditSeverity.Info;
    public List<string> Flags { get; init; } = [];
    public string? Details { get; init; }
    public string? SourceIp { get; init; }
}

/// <summary>Параметры события, которые задаёт код операции.</summary>
public sealed record AuditEntry(
    string Action,
    string Result,
    string? TargetType = null,
    string? TargetId = null,
    string? Details = null,
    string? Severity = null,
    IReadOnlyCollection<string>? Flags = null,
    AuditActor? Actor = null);

/// <summary>
/// Явно заданный субъект действия. Нужен, когда пользователь ещё не аутентифицирован,
/// например при попытке входа.
/// </summary>
public sealed record AuditActor(Guid? Id, string? Login, string? Role);

public static class AuditResults
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failed = "failed";
}

public static class AuditSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
}

public static class AuditFlags
{
    public const string OffHours = "off_hours";
    public const string MultipleFailedLogins = "multiple_failed_logins";
    public const string AccountLocked = "account_locked";
    public const string ForeignRequestAccess = "foreign_request_access";
}
