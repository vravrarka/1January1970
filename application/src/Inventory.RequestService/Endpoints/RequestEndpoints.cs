using Inventory.RequestService.Data;
using Inventory.RequestService.Services;
using Inventory.Shared.Auditing;
using Inventory.Shared.Domain;
using Inventory.Shared.Http;
using Inventory.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Inventory.RequestService.Endpoints;

public static class RequestEndpoints
{
    private const string DeviceServiceDown = "Сервис устройств временно недоступен. Повторите позже.";

    public static IEndpointRouteBuilder MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/requests").WithTags("Requests");

        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Student);
        group.MapGet("/my", ListMineAsync).RequireAuthorization(Policies.Student);
        group.MapGet("/", ListForTeacherAsync).RequireAuthorization(Policies.Teacher);
        group.MapGet("/{requestId:guid}", GetAsync).RequireAuthorization(Policies.TeacherOrStudent);
        group.MapPatch("/{requestId:guid}/review", ReviewAsync).RequireAuthorization(Policies.Teacher);

        // Шлюз направляет PATCH /devices/{id}/return сюда: возврат закрывает заявку
        // и освобождает устройство, поэтому оркестрирует его сервис заявок.
        app.MapPatch("/devices/{deviceId:guid}/return", ConfirmReturnAsync)
            .RequireAuthorization(Policies.Teacher)
            .WithTags("Requests");

        return app;
    }

    /// <summary>Создание заявки учеником (сценарий 1, SR-08).</summary>
    private static async Task<IResult> CreateAsync(
        CreateLoanRequest request,
        HttpContext http,
        RequestDbContext db,
        DeviceServiceClient devices,
        IAuditRecorder audit,
        TimeProvider time,
        ILogger<LoanRequest> logger,
        CancellationToken ct)
    {
        if (request.DeviceId is not { } deviceId || deviceId == Guid.Empty)
        {
            return ApiErrors.BadRequest("invalid_device_id", "Поле deviceId обязательно.");
        }

        if (request.Comment is { Length: > 500 })
        {
            return ApiErrors.BadRequest("invalid_comment", "Комментарий не длиннее 500 символов.");
        }

        // Ученик определяется из токена, а не из тела запроса.
        var studentId = http.User.GetUserId();

        DeviceInfo? device;
        IReadOnlyList<DeviceInfo> held;
        try
        {
            device = await devices.GetDeviceAsync(deviceId, ct);
            if (device is null)
            {
                return ApiErrors.NotFound("device_not_found", "Устройство не найдено.");
            }

            held = await devices.GetHeldDevicesAsync(studentId, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Ошибка обращения к сервису устройств");
            return ApiErrors.Unavailable("device_service_unavailable", DeviceServiceDown);
        }

        if (device.Status != DeviceStatus.Available)
        {
            return ApiErrors.Conflict("device_not_available", "Устройство сейчас недоступно для выдачи.");
        }

        var pending = await db.LoanRequests
            .Where(r => r.StudentId == studentId && r.Status == RequestStatus.Pending)
            .ToListAsync(ct);

        if (pending.Any(r => r.DeviceId == deviceId))
        {
            return ApiErrors.Conflict("duplicate_request", "У вас уже есть заявка на это устройство, ожидающая рецензии.");
        }

        // Предварительная проверка SR-08 с учётом уже выданных устройств и заявок,
        // ожидающих решения. Окончательная проверка выполняется при одобрении (D-02).
        var occupied = held.Select(d => d.Type).Concat(pending.Select(r => r.DeviceType)).ToList();
        var check = IssuancePolicy.Check(occupied, device.Type);
        if (!check.Allowed)
        {
            await audit.RecordNowAsync(new AuditEntry("request.create", AuditResults.Denied, "device", deviceId.ToString(),
                Details: $"[{check.Code}] {check.Message}"), ct);
            return ApiErrors.Conflict(check.Code!, check.Message!);
        }

        var loan = new LoanRequest
        {
            StudentId = studentId,
            StudentLogin = http.User.GetLogin(),
            StudentName = http.User.GetFullName(),
            DeviceId = device.Id,
            DeviceName = device.Name,
            DeviceType = device.Type,
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            CreatedAt = time.GetUtcNow()
        };

        db.LoanRequests.Add(loan);
        audit.Record(new AuditEntry("request.created", AuditResults.Success, "request", loan.Id.ToString(),
            Details: $"Заявка на устройство «{device.Name}» ({device.Id})"));
        await db.SaveChangesAsync(ct);

        return Results.Created($"/requests/{loan.Id}", LoanRequestDto.From(loan));
    }

    /// <summary>D-03 / SR-09: выборка строго по идентификатору ученика из токена.</summary>
    private static async Task<IResult> ListMineAsync(HttpContext http, RequestDbContext db, CancellationToken ct)
    {
        var studentId = http.User.GetUserId();
        var requests = await db.LoanRequests.AsNoTracking()
            .Where(r => r.StudentId == studentId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(requests.Select(LoanRequestDto.From));
    }

    private static async Task<IResult> ListForTeacherAsync(string? status, Guid? studentId, RequestDbContext db, CancellationToken ct)
    {
        var query = db.LoanRequests.AsNoTracking();

        if (status is not null)
        {
            if (!EnumNames.TryParse<RequestStatus>(status, out var parsed))
            {
                return ApiErrors.BadRequest("invalid_status", $"Допустимые значения status: {EnumNames.Allowed<RequestStatus>()}.");
            }

            query = query.Where(r => r.Status == parsed);
        }

        if (studentId is { } sid)
        {
            query = query.Where(r => r.StudentId == sid);
        }

        var requests = await query.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
        return Results.Ok(requests.Select(LoanRequestDto.From));
    }

    private static async Task<IResult> GetAsync(Guid requestId, HttpContext http, RequestDbContext db, IAuditRecorder audit, CancellationToken ct)
    {
        var request = await db.LoanRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return ApiErrors.NotFound("request_not_found", "Заявка не найдена.");
        }

        // SR-09: попытка ученика посмотреть чужую заявку отклоняется с 403 и фиксируется.
        if (http.User.GetRole() == Roles.Student && request.StudentId != http.User.GetUserId())
        {
            await audit.RecordNowAsync(new AuditEntry("request.view", AuditResults.Denied, "request", requestId.ToString(),
                Details: "Попытка просмотра чужой заявки",
                Severity: AuditSeverity.Warning,
                Flags: [AuditFlags.ForeignRequestAccess]), ct);
            return ApiErrors.Forbidden("foreign_request", "Можно просматривать только собственные заявки.");
        }

        return Results.Ok(LoanRequestDto.From(request));
    }

    /// <summary>Рецензирование заявки учителем (SR-07, D-02).</summary>
    private static async Task<IResult> ReviewAsync(
        Guid requestId,
        ReviewRequest body,
        HttpContext http,
        RequestDbContext db,
        DeviceServiceClient devices,
        IAuditRecorder audit,
        TimeProvider time,
        ILogger<LoanRequest> logger,
        CancellationToken ct)
    {
        if (body.Decision is not { } decision)
        {
            return ApiErrors.BadRequest("invalid_decision", "Поле decision обязательно: approved или rejected.");
        }

        if (body.Comment is { Length: > 500 })
        {
            return ApiErrors.BadRequest("invalid_comment", "Комментарий не длиннее 500 символов.");
        }

        var request = await db.LoanRequests.SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return ApiErrors.NotFound("request_not_found", "Заявка не найдена.");
        }

        if (request.Status != RequestStatus.Pending)
        {
            return ApiErrors.Conflict("request_already_reviewed",
                $"Заявка уже рассмотрена (статус {EnumNames.ToApi(request.Status)}).");
        }

        var now = time.GetUtcNow();
        var reviewerId = http.User.GetUserId();
        var reviewerName = http.User.GetFullName();
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();

        if (decision == ReviewDecision.Rejected)
        {
            MarkReviewed(request, RequestStatus.Rejected, reviewerId, reviewerName, comment, now);
            audit.Record(new AuditEntry("request.rejected", AuditResults.Success, "request", request.Id.ToString(),
                Details: $"Заявка ученика {request.StudentLogin} на «{request.DeviceName}» отклонена"));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ApiErrors.Conflict("request_changed_concurrently", "Заявка была изменена параллельно. Обновите данные.");
            }

            return Results.Ok(LoanRequestDto.From(request));
        }

        // Одобрение: сервис устройств под блокировкой повторно проверяет ограничение SR-08
        // на актуальном состоянии и закрепляет устройство за учеником.
        DeviceCallResult issue;
        try
        {
            issue = await devices.IssueAsync(request.DeviceId, request.StudentId, request.StudentName, request.Id, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Ошибка выдачи устройства {DeviceId}", request.DeviceId);
            return ApiErrors.Unavailable("device_service_unavailable", DeviceServiceDown);
        }

        if (issue.Outcome != CallOutcome.Success)
        {
            var code = issue.Code ?? (issue.Outcome == CallOutcome.NotFound ? "device_not_found" : "issue_denied");
            var message = issue.Message ?? "Выдача устройства невозможна.";
            await audit.RecordNowAsync(new AuditEntry("request.approve", AuditResults.Denied, "request", request.Id.ToString(),
                Details: $"[{code}] {message}"), ct);
            return ApiErrors.Conflict(code, message);
        }

        MarkReviewed(request, RequestStatus.Approved, reviewerId, reviewerName, comment, now);

        var competing = await db.LoanRequests
            .Where(r => r.DeviceId == request.DeviceId && r.Status == RequestStatus.Pending && r.Id != request.Id)
            .ToListAsync(ct);
        foreach (var other in competing)
        {
            MarkReviewed(other, RequestStatus.Rejected, reviewerId, reviewerName,
                "Автоматически: устройство выдано по другой заявке.", now);
        }

        audit.Record(new AuditEntry("request.approved", AuditResults.Success, "request", request.Id.ToString(),
            Details: $"Заявка ученика {request.StudentLogin} на «{request.DeviceName}» одобрена, устройство выдано" +
                     (competing.Count > 0 ? $"; автоматически отклонено конкурирующих заявок: {competing.Count}" : string.Empty)));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Компенсация: устройство уже выдано, но заявку сохранить не удалось.
            logger.LogWarning(ex, "Не удалось сохранить одобрение заявки {RequestId}, отменяем выдачу", request.Id);
            try
            {
                await devices.ReleaseAsync(request.DeviceId, request.StudentId, request.Id, CancellationToken.None);
            }
            catch (Exception compensationError)
            {
                logger.LogError(compensationError,
                    "Компенсация не удалась: устройство {DeviceId} осталось выданным без одобренной заявки", request.DeviceId);
            }

            return ApiErrors.Conflict("request_changed_concurrently", "Заявка была изменена параллельно. Обновите данные.");
        }

        return Results.Ok(LoanRequestDto.From(request));
    }

    /// <summary>Подтверждение возврата устройства учителем (сценарий 2).</summary>
    private static async Task<IResult> ConfirmReturnAsync(
        Guid deviceId,
        HttpContext http,
        RequestDbContext db,
        DeviceServiceClient devices,
        IAuditRecorder audit,
        TimeProvider time,
        ILogger<LoanRequest> logger,
        CancellationToken ct)
    {
        var request = await db.LoanRequests
            .SingleOrDefaultAsync(r => r.DeviceId == deviceId && r.Status == RequestStatus.Approved, ct);
        if (request is null)
        {
            return ApiErrors.Conflict("device_not_issued", "По этому устройству нет активной выдачи.");
        }

        var reconciled = false;
        try
        {
            var release = await devices.ReleaseAsync(deviceId, request.StudentId, request.Id, ct);
            if (release.Outcome != CallOutcome.Success)
            {
                // Если устройство уже освобождено (например, прошлый вызов прервался после
                // освобождения), закрываем заявку, чтобы состояния сервисов сошлись.
                var device = await devices.GetDeviceAsync(deviceId, ct);
                if (release.Code == "device_not_issued_to_student" && device is { Status: not DeviceStatus.Issued })
                {
                    reconciled = true;
                }
                else
                {
                    return ApiErrors.Conflict(release.Code ?? "return_denied", release.Message ?? "Возврат невозможен.");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Ошибка возврата устройства {DeviceId}", deviceId);
            return ApiErrors.Unavailable("device_service_unavailable", DeviceServiceDown);
        }

        request.Status = RequestStatus.Returned;
        request.ReturnedAt = time.GetUtcNow();
        request.ReturnConfirmedById = http.User.GetUserId();
        request.ReturnConfirmedByName = http.User.GetFullName();

        audit.Record(new AuditEntry("request.return_confirmed", AuditResults.Success, "request", request.Id.ToString(),
            Details: $"Возврат устройства «{request.DeviceName}» от ученика {request.StudentLogin} подтверждён" +
                     (reconciled ? " (состояние согласовано с сервисом устройств)" : string.Empty)));
        await db.SaveChangesAsync(ct);

        return Results.Ok(LoanRequestDto.From(request));
    }

    private static void MarkReviewed(LoanRequest request, RequestStatus status, Guid reviewerId, string reviewerName,
        string? comment, DateTimeOffset now)
    {
        request.Status = status;
        request.ReviewerId = reviewerId;
        request.ReviewerName = reviewerName;
        request.ReviewComment = comment;
        request.ReviewedAt = now;
    }
}
