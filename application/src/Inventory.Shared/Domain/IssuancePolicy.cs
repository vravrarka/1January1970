namespace Inventory.Shared.Domain;

/// <summary>
/// Предметное ограничение SR-08 / D-02: у одного ученика одновременно не более двух
/// устройств, и все они должны быть разных типов.
/// Политика чистая (без ввода-вывода), поэтому её используют и сервис заявок
/// (предварительная проверка при создании), и сервис устройств (окончательная
/// проверка под блокировкой при выдаче).
/// </summary>
public static class IssuancePolicy
{
    public const int MaxDevicesPerStudent = 2;

    public const string LimitReachedCode = "device_limit_reached";
    public const string DuplicateTypeCode = "duplicate_device_type";

    /// <param name="occupiedTypes">Типы устройств, уже выданных ученику (и, при необходимости, зарезервированных заявками).</param>
    /// <param name="requestedType">Тип запрашиваемого устройства.</param>
    public static IssuanceCheck Check(IReadOnlyCollection<DeviceType> occupiedTypes, DeviceType requestedType)
    {
        ArgumentNullException.ThrowIfNull(occupiedTypes);

        if (occupiedTypes.Count >= MaxDevicesPerStudent)
        {
            return IssuanceCheck.Deny(
                LimitReachedCode,
                $"У ученика уже {occupiedTypes.Count} устройства (выданных или ожидающих выдачи). " +
                $"Одновременно допускается не более {MaxDevicesPerStudent}. Сначала нужно вернуть устройство.");
        }

        if (occupiedTypes.Contains(requestedType))
        {
            return IssuanceCheck.Deny(
                DuplicateTypeCode,
                $"У ученика уже есть устройство типа «{DeviceTypeTitles.Russian[requestedType]}». " +
                "Устройства одного ученика должны быть разных типов.");
        }

        return IssuanceCheck.Allow;
    }
}

public sealed record IssuanceCheck(bool Allowed, string? Code, string? Message)
{
    public static readonly IssuanceCheck Allow = new(true, null, null);

    public static IssuanceCheck Deny(string code, string message) => new(false, code, message);
}
