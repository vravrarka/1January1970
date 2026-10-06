using System.Net;
using System.Net.Http.Json;
using Inventory.Shared.Domain;
using Inventory.Shared.Http;

namespace Inventory.RequestService.Services;

public sealed record DeviceInfo(Guid Id, string Name, DeviceType Type, DeviceStatus Status);

public enum CallOutcome
{
    Success,
    NotFound,
    Conflict
}

public sealed record DeviceCallResult(CallOutcome Outcome, string? Code = null, string? Message = null)
{
    public static readonly DeviceCallResult Ok = new(CallOutcome.Success);
}

/// <summary>
/// Клиент внутреннего API сервиса устройств. Токен текущего пользователя передаётся
/// дальше, поэтому сервис устройств сам проверяет роль инициатора.
/// Сетевые ошибки и ответы 5xx выбрасываются как <see cref="HttpRequestException"/>.
/// </summary>
public sealed class DeviceServiceClient(HttpClient http)
{
    public async Task<DeviceInfo?> GetDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"devices/{deviceId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceInfo>(JsonDefaults.Options, ct);
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetHeldDevicesAsync(Guid studentId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"internal/holders/{studentId}/devices", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DeviceInfo>>(JsonDefaults.Options, ct) ?? [];
    }

    public Task<DeviceCallResult> IssueAsync(Guid deviceId, Guid studentId, string studentName, Guid requestId, CancellationToken ct) =>
        PostAsync($"internal/devices/{deviceId}/issue", new { studentId, studentName, requestId }, ct);

    public Task<DeviceCallResult> ReleaseAsync(Guid deviceId, Guid expectedHolderId, Guid requestId, CancellationToken ct) =>
        PostAsync($"internal/devices/{deviceId}/release", new { expectedHolderId, requestId }, ct);

    private async Task<DeviceCallResult> PostAsync(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, JsonDefaults.Options, ct);
        if (response.IsSuccessStatusCode)
        {
            return DeviceCallResult.Ok;
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            var problem = await TryReadProblemAsync(response, ct);
            return new DeviceCallResult(
                response.StatusCode == HttpStatusCode.NotFound ? CallOutcome.NotFound : CallOutcome.Conflict,
                problem?.Code,
                problem?.Title);
        }

        // 401/403 означают, что у инициатора нет прав, это ошибка конфигурации или атаки.
        throw new HttpRequestException(
            $"Сервис устройств ответил {(int)response.StatusCode} на {path}", null, response.StatusCode);
    }

    private static async Task<RemoteProblem?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<RemoteProblem>(JsonDefaults.Options, ct);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
