using Inventory.RequestService.Data;
using Inventory.Shared.Domain;

namespace Inventory.RequestService.Endpoints;

public enum ReviewDecision
{
    Approved,
    Rejected
}

public sealed record CreateLoanRequest(Guid? DeviceId, string? Comment);

public sealed record ReviewRequest(ReviewDecision? Decision, string? Comment);

public sealed record PersonDto(Guid Id, string? Name);

public sealed record StudentDto(Guid Id, string Login, string Name);

public sealed record RequestDeviceDto(Guid Id, string Name, DeviceType Type);

public sealed record LoanRequestDto(
    Guid Id,
    StudentDto Student,
    RequestDeviceDto Device,
    RequestStatus Status,
    string? Comment,
    DateTimeOffset CreatedAt,
    PersonDto? Reviewer,
    string? ReviewComment,
    DateTimeOffset? ReviewedAt,
    PersonDto? ReturnConfirmedBy,
    DateTimeOffset? ReturnedAt)
{
    public static LoanRequestDto From(LoanRequest r) => new(
        r.Id,
        new StudentDto(r.StudentId, r.StudentLogin, r.StudentName),
        new RequestDeviceDto(r.DeviceId, r.DeviceName, r.DeviceType),
        r.Status,
        r.Comment,
        r.CreatedAt,
        r.ReviewerId is { } reviewer ? new PersonDto(reviewer, r.ReviewerName) : null,
        r.ReviewComment,
        r.ReviewedAt,
        r.ReturnConfirmedById is { } confirmer ? new PersonDto(confirmer, r.ReturnConfirmedByName) : null,
        r.ReturnedAt);
}
