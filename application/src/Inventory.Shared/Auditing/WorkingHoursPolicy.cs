using Microsoft.Extensions.Options;

namespace Inventory.Shared.Auditing;

public sealed class WorkingHoursOptions
{
    /// <summary>Часовой пояс школы (IANA), например <c>Europe/Moscow</c>.</summary>
    public string TimeZone { get; set; } = "Europe/Moscow";

    /// <summary>Начало учебного времени, час (включительно).</summary>
    public int StartHour { get; set; } = 8;

    /// <summary>Конец учебного времени, час (не включительно).</summary>
    public int EndHour { get; set; } = 18;

    /// <summary>Учебные дни через запятую: Mon,Tue,Wed,Thu,Fri,Sat,Sun.</summary>
    public string WorkDays { get; set; } = "Mon,Tue,Wed,Thu,Fri";
}

/// <summary>
/// SR-02: действия во внеучебное время логируются с пометкой-предупреждением.
/// </summary>
public sealed class WorkingHoursPolicy
{
    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday,
        ["Sun"] = DayOfWeek.Sunday
    };

    private readonly TimeZoneInfo _timeZone;
    private readonly int _startHour;
    private readonly int _endHour;
    private readonly HashSet<DayOfWeek> _workDays;

    public WorkingHoursPolicy(IOptions<WorkingHoursOptions> options)
        : this(options.Value)
    {
    }

    public WorkingHoursPolicy(WorkingHoursOptions options)
    {
        if (options.StartHour is < 0 or > 23 || options.EndHour is < 1 or > 24 || options.StartHour >= options.EndHour)
        {
            throw new InvalidOperationException("WorkingHours: требуется 0 <= StartHour < EndHour <= 24.");
        }

        _timeZone = TimeZoneInfo.TryFindSystemTimeZoneById(options.TimeZone, out var tz) ? tz : TimeZoneInfo.Utc;
        _startHour = options.StartHour;
        _endHour = options.EndHour;
        _workDays = options.WorkDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => DayNames.TryGetValue(d, out var day)
                ? day
                : throw new InvalidOperationException($"WorkingHours: неизвестный день недели «{d}»."))
            .ToHashSet();
    }

    public TimeZoneInfo TimeZone => _timeZone;

    public bool IsOffHours(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, _timeZone);
        if (!_workDays.Contains(local.DayOfWeek))
        {
            return true;
        }

        return local.Hour < _startHour || local.Hour >= _endHour;
    }
}
