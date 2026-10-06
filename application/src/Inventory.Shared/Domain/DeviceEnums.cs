namespace Inventory.Shared.Domain;

/// <summary>
/// Фиксированный список типов устройств. Добавление новых типов через API
/// сознательно не поддерживается (см. PROJECT.md, «Команда сознательно не реализует»).
/// </summary>
public enum DeviceType
{
    Laptop,
    Tablet,
    Smartphone,
    Monitor,
    Camera,
    VrHeadset,
    Microcontroller,
    Router
}

public enum DeviceStatus
{
    /// <summary>Устройство на хранении и доступно для выдачи.</summary>
    Available,

    /// <summary>Устройство выдано ученику. Устанавливается только через одобрение заявки.</summary>
    Issued,

    /// <summary>Устройство на обслуживании и недоступно для выдачи.</summary>
    Maintenance,

    /// <summary>Устройство списано.</summary>
    Decommissioned
}

public static class DeviceTypeTitles
{
    public static readonly IReadOnlyDictionary<DeviceType, string> Russian = new Dictionary<DeviceType, string>
    {
        [DeviceType.Laptop] = "Ноутбук",
        [DeviceType.Tablet] = "Планшет",
        [DeviceType.Smartphone] = "Смартфон",
        [DeviceType.Monitor] = "Монитор",
        [DeviceType.Camera] = "Камера",
        [DeviceType.VrHeadset] = "VR-гарнитура",
        [DeviceType.Microcontroller] = "Микроконтроллер",
        [DeviceType.Router] = "Маршрутизатор"
    };
}
