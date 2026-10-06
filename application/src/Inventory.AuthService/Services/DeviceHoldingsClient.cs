using System.Net.Http.Json;
using Inventory.Shared.Http;

namespace Inventory.AuthService.Services;

/// <summary>
/// Запрашивает у сервиса устройств, есть ли у пользователя выданные устройства.
/// Удалять ученика с выданным оборудованием нельзя: устройство «потеряется» в системе.
/// </summary>
public sealed class DeviceHoldingsClient(HttpClient http)
{
    private sealed record HeldDevice(Guid Id, string Name);

    public async Task<int> CountHeldDevicesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var devices = await http.GetFromJsonAsync<List<HeldDevice>>(
            $"internal/holders/{userId}/devices", JsonDefaults.Options, cancellationToken);
        return devices?.Count ?? 0;
    }
}
