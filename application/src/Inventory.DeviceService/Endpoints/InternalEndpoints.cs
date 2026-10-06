using Inventory.DeviceService.Data;
using Inventory.Shared.Auditing;
using Inventory.Shared.Domain;
using Inventory.Shared.Http;
using Inventory.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inventory.DeviceService.Endpoints;

/// <summary>
/// Внутренние операции для других сервисов. Шлюз их не публикует, но они всё равно
/// требуют токен пользователя-инициатора и проверяют его роль (D-01, защита в глубину).
/// </summary>
public static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal").WithTags("Internal");

        group.MapGet("/holders/{studentId:guid}/devices", GetHeldDevicesAsync).RequireAuthorization(Policies.AnyRole);
        group.MapPost("/devices/{deviceId:guid}/issue", IssueAsync).RequireAuthorization(Policies.Teacher);
        group.MapPost("/devices/{deviceId:guid}/release", ReleaseAsync).RequireAuthorization(Policies.Teacher);

        return app;
    }

    private static async Task<IResult> GetHeldDevicesAsync(Guid studentId, HttpContext http, DeviceDbContext db, CancellationToken ct)
    {
        // Ученик может узнать только о своих устройствах.
        if (http.User.GetRole() == Roles.Student && http.User.GetUserId() != studentId)
        {
            return ApiErrors.Forbidden("foreign_holder", "Можно запрашивать только собственные устройства.");
        }

        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.HolderId == studentId)
            .OrderBy(d => d.IssuedAt)
            .ToListAsync(ct);
        return Results.Ok(devices.Select(DeviceDto.From));
    }

    /// <summary>
    /// D-02: окончательная проверка ограничения SR-08 в момент выдачи на актуальном
    /// состоянии, под блокировками ученика и устройства.
    /// </summary>
    private static async Task<IResult> IssueAsync(
        Guid deviceId,
        IssueDeviceRequest request,
        DeviceDbContext db,
        IAuditRecorder audit,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request.StudentId == Guid.Empty || request.RequestId == Guid.Empty)
        {
            return ApiErrors.BadRequest("invalid_input", "Требуются studentId и requestId.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await DeviceLocks.LockStudentAsync(db, request.StudentId, ct);
        var device = await DeviceLocks.LockDeviceAsync(db, deviceId, ct);

        if (device is null)
        {
            return ApiErrors.NotFound("device_not_found", "Устройство не найдено.");
        }

        if (device.Status != DeviceStatus.Available)
        {
            await DenyAsync(db, audit, tx, device, request, "device_not_available",
                $"Устройство недоступно для выдачи (статус {EnumNames.ToApi(device.Status)}).", ct);
            return ApiErrors.Conflict("device_not_available",
                $"Устройство недоступно для выдачи (статус {EnumNames.ToApi(device.Status)}).");
        }

        var heldTypes = await db.Devices
            .Where(d => d.HolderId == request.StudentId)
            .Select(d => d.Type)
            .ToListAsync(ct);

        var check = IssuancePolicy.Check(heldTypes, device.Type);
        if (!check.Allowed)
        {
            await DenyAsync(db, audit, tx, device, request, check.Code!, check.Message!, ct);
            return ApiErrors.Conflict(check.Code!, check.Message!);
        }

        var now = time.GetUtcNow();
        device.Status = DeviceStatus.Issued;
        device.HolderId = request.StudentId;
        device.HolderName = request.StudentName?.Trim() is { Length: > 0 and <= 200 } name ? name : null;
        device.IssuedByRequestId = request.RequestId;
        device.IssuedAt = now;
        device.UpdatedAt = now;

        audit.Record(new AuditEntry("device.issued", AuditResults.Success, "device", device.Id.ToString(),
            Details: $"Устройство «{device.Name}» выдано ученику {request.StudentId} по заявке {request.RequestId}"));

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return ApiErrors.Conflict(IssuancePolicy.DuplicateTypeCode,
                "У ученика уже есть устройство этого типа.");
        }

        return Results.Ok(DeviceDto.From(device));
    }

    private static async Task<IResult> ReleaseAsync(
        Guid deviceId,
        ReleaseDeviceRequest request,
        DeviceDbContext db,
        IAuditRecorder audit,
        TimeProvider time,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var device = await DeviceLocks.LockDeviceAsync(db, deviceId, ct);
        if (device is null)
        {
            return ApiErrors.NotFound("device_not_found", "Устройство не найдено.");
        }

        if (device.Status != DeviceStatus.Issued || device.HolderId != request.ExpectedHolderId)
        {
            return ApiErrors.Conflict("device_not_issued_to_student",
                "Устройство не числится за указанным учеником.");
        }

        var previousHolder = device.HolderId;
        device.Status = DeviceStatus.Available;
        device.HolderId = null;
        device.HolderName = null;
        device.IssuedByRequestId = null;
        device.IssuedAt = null;
        device.UpdatedAt = time.GetUtcNow();

        audit.Record(new AuditEntry("device.returned", AuditResults.Success, "device", device.Id.ToString(),
            Details: $"Устройство «{device.Name}» возвращено учеником {previousHolder}" +
                     (request.RequestId is { } rid ? $", заявка {rid}" : string.Empty)));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(DeviceDto.From(device));
    }

    private static async Task DenyAsync(
        DeviceDbContext db,
        IAuditRecorder audit,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        Device device,
        IssueDeviceRequest request,
        string code,
        string message,
        CancellationToken ct)
    {
        audit.Record(new AuditEntry("device.issue", AuditResults.Denied, "device", device.Id.ToString(),
            Details: $"[{code}] {message} Ученик {request.StudentId}, заявка {request.RequestId}"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
