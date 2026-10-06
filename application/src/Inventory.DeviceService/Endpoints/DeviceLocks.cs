using Inventory.DeviceService.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.DeviceService.Endpoints;

/// <summary>
/// Блокировки PostgreSQL, которые сериализуют конкурирующие операции (T-08).
/// Вызывать только внутри открытой транзакции: блокировки снимаются при её завершении.
/// </summary>
public static class DeviceLocks
{
    /// <summary>Блокирует строку устройства до конца транзакции (SELECT ... FOR UPDATE).</summary>
    public static async Task<Device?> LockDeviceAsync(DeviceDbContext db, Guid deviceId, CancellationToken ct)
    {
        var rows = await db.Devices
            .FromSql($"SELECT * FROM devices WHERE id = {deviceId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Транзакционная advisory-блокировка на ученика. Параллельные выдачи разных устройств
    /// одному ученику выполняются строго по очереди, поэтому обе не могут пройти проверку
    /// лимита на одном и том же «старом» состоянии.
    /// </summary>
    public static Task LockStudentAsync(DeviceDbContext db, Guid studentId, CancellationToken ct)
    {
        var key = StudentLockKey(studentId);
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    public static long StudentLockKey(Guid studentId)
    {
        Span<byte> bytes = stackalloc byte[16];
        studentId.TryWriteBytes(bytes);
        return BitConverter.ToInt64(bytes[..8]) ^ BitConverter.ToInt64(bytes[8..]);
    }
}
