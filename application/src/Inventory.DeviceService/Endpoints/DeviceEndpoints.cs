using Inventory.DeviceService.Data;
using Inventory.Shared.Auditing;
using Inventory.Shared.Domain;
using Inventory.Shared.Http;
using Inventory.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Inventory.DeviceService.Endpoints;

/// <summary>Публичные операции с устройствами (доступны через шлюз).</summary>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/device-types", () => Results.Ok(
                Enum.GetValues<DeviceType>().Select(t => new { code = EnumNames.ToApi(t), title = DeviceTypeTitles.Russian[t] })))
            .RequireAuthorization(Policies.AnyRole)
            .WithTags("Devices");

        var group = app.MapGroup("/devices").WithTags("Devices");

        // SR-06: просматривать список могут все роли, изменять — только администратор.
        group.MapGet("/", ListAsync).RequireAuthorization(Policies.AnyRole);
        group.MapGet("/{deviceId:guid}", GetAsync).RequireAuthorization(Policies.AnyRole);
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.Admin);
        group.MapPatch("/{deviceId:guid}", UpdateAsync).RequireAuthorization(Policies.Admin);

        return app;
    }

    private static async Task<IResult> ListAsync(string? status, string? type, DeviceDbContext db, CancellationToken ct)
    {
        var query = db.Devices.AsNoTracking();

        if (status is not null)
        {
            if (!EnumNames.TryParse<DeviceStatus>(status, out var parsed))
            {
                return ApiErrors.BadRequest("invalid_status", $"Допустимые значения status: {EnumNames.Allowed<DeviceStatus>()}.");
            }

            query = query.Where(d => d.Status == parsed);
        }

        if (type is not null)
        {
            if (!EnumNames.TryParse<DeviceType>(type, out var parsed))
            {
                return ApiErrors.BadRequest("invalid_type", $"Допустимые значения type: {EnumNames.Allowed<DeviceType>()}.");
            }

            query = query.Where(d => d.Type == parsed);
        }

        var devices = await query.OrderBy(d => d.Type).ThenBy(d => d.Name).ToListAsync(ct);
        return Results.Ok(devices.Select(DeviceDto.From));
    }

    private static async Task<IResult> GetAsync(Guid deviceId, DeviceDbContext db, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId, ct);
        return device is null
            ? ApiErrors.NotFound("device_not_found", "Устройство не найдено.")
            : Results.Ok(DeviceDto.From(device));
    }

    private static async Task<IResult> CreateAsync(
        CreateDeviceRequest request,
        DeviceDbContext db,
        IAuditRecorder audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            return ApiErrors.BadRequest("invalid_name", "Поле name обязательно, не длиннее 200 символов.");
        }

        if (request.Type is not { } type)
        {
            return ApiErrors.BadRequest("invalid_type", $"Поле type обязательно: {EnumNames.Allowed<DeviceType>()}.");
        }

        if (request.Description is { Length: > 1000 })
        {
            return ApiErrors.BadRequest("invalid_description", "Поле description не длиннее 1000 символов.");
        }

        var status = request.Status ?? DeviceStatus.Available;
        if (status is not (DeviceStatus.Available or DeviceStatus.Maintenance))
        {
            return ApiErrors.BadRequest("invalid_status", "Новое устройство может иметь статус available или maintenance.");
        }

        var now = time.GetUtcNow();
        var device = new Device
        {
            Name = name,
            Type = type,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Devices.Add(device);
        audit.Record(new AuditEntry("device.created", AuditResults.Success, "device", device.Id.ToString(),
            Details: $"Добавлено устройство «{device.Name}», тип {EnumNames.ToApi(type)}, статус {EnumNames.ToApi(status)}"));
        await db.SaveChangesAsync(ct);

        return Results.Created($"/devices/{device.Id}", DeviceDto.From(device));
    }

    private static async Task<IResult> UpdateAsync(
        Guid deviceId,
        UpdateDeviceRequest request,
        DeviceDbContext db,
        IAuditRecorder audit,
        TimeProvider time,
        CancellationToken ct)
    {
        if (request is { Name: null, Type: null, Description: null, Status: null })
        {
            return ApiErrors.BadRequest("empty_update", "Передайте хотя бы одно из полей: name, type, description, status.");
        }

        if (request.Name is not null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200))
        {
            return ApiErrors.BadRequest("invalid_name", "Поле name не может быть пустым и длиннее 200 символов.");
        }

        if (request.Description is { Length: > 1000 })
        {
            return ApiErrors.BadRequest("invalid_description", "Поле description не длиннее 1000 символов.");
        }

        if (request.Status == DeviceStatus.Issued)
        {
            return ApiErrors.BadRequest("status_not_assignable",
                "Статус issued устанавливается только при одобрении заявки учителем.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var device = await DeviceLocks.LockDeviceAsync(db, deviceId, ct);
        if (device is null)
        {
            return ApiErrors.NotFound("device_not_found", "Устройство не найдено.");
        }

        var statusChanges = request.Status is { } s && s != device.Status;
        var typeChanges = request.Type is { } t && t != device.Type;
        if (device.Status == DeviceStatus.Issued && (statusChanges || typeChanges))
        {
            return ApiErrors.Conflict("device_is_issued",
                "Устройство выдано ученику. Изменить статус или тип можно только после подтверждения возврата.");
        }

        var changes = new List<string>();
        var oldStatus = device.Status;

        if (request.Name is not null && request.Name.Trim() != device.Name)
        {
            changes.Add($"name: «{device.Name}» → «{request.Name.Trim()}»");
            device.Name = request.Name.Trim();
        }

        if (request.Description is not null)
        {
            var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            if (description != device.Description)
            {
                changes.Add("description");
                device.Description = description;
            }
        }

        if (typeChanges)
        {
            changes.Add($"type: {EnumNames.ToApi(device.Type)} → {EnumNames.ToApi(request.Type!.Value)}");
            device.Type = request.Type!.Value;
        }

        if (statusChanges)
        {
            changes.Add($"status: {EnumNames.ToApi(oldStatus)} → {EnumNames.ToApi(request.Status!.Value)}");
            device.Status = request.Status!.Value;
        }

        if (changes.Count == 0)
        {
            return Results.Ok(DeviceDto.From(device));
        }

        device.UpdatedAt = time.GetUtcNow();
        audit.Record(new AuditEntry(
            statusChanges ? "device.status_changed" : "device.updated",
            AuditResults.Success, "device", device.Id.ToString(),
            Details: string.Join("; ", changes)));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(DeviceDto.From(device));
    }
}
