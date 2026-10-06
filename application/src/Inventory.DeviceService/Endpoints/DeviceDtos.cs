using Inventory.DeviceService.Data;
using Inventory.Shared.Domain;

namespace Inventory.DeviceService.Endpoints;

public sealed record HolderDto(Guid Id, string? Name);

public sealed record DeviceDto(
    Guid Id,
    string Name,
    DeviceType Type,
    string? Description,
    DeviceStatus Status,
    bool InStorage,
    HolderDto? Holder,
    DateTimeOffset? IssuedAt,
    DateTimeOffset UpdatedAt)
{
    public static DeviceDto From(Device d) => new(
        d.Id,
        d.Name,
        d.Type,
        d.Description,
        d.Status,
        d.HolderId is null,
        d.HolderId is { } holder ? new HolderDto(holder, d.HolderName) : null,
        d.IssuedAt,
        d.UpdatedAt);
}

public sealed record CreateDeviceRequest(string? Name, DeviceType? Type, string? Description, DeviceStatus? Status);

public sealed record UpdateDeviceRequest(string? Name, DeviceType? Type, string? Description, DeviceStatus? Status);

public sealed record IssueDeviceRequest(Guid StudentId, string? StudentName, Guid RequestId);

public sealed record ReleaseDeviceRequest(Guid ExpectedHolderId, Guid? RequestId);
