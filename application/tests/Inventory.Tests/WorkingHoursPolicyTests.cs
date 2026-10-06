using Inventory.Shared.Auditing;

namespace Inventory.Tests;

/// <summary>SR-02: действия во внеучебное время помечаются предупреждением.</summary>
public class WorkingHoursPolicyTests
{
    // Europe/Moscow — постоянно UTC+3, переходов на летнее время нет.
    private static readonly WorkingHoursPolicy Policy = new(new WorkingHoursOptions
    {
        TimeZone = "Europe/Moscow",
        StartHour = 8,
        EndHour = 18,
        WorkDays = "Mon,Tue,Wed,Thu,Fri"
    });

    [Theory]
    [InlineData("2026-10-05T07:00:00Z")] // понедельник, 10:00 МСК
    [InlineData("2026-10-05T05:00:00Z")] // понедельник, 08:00 МСК, граница включительно
    [InlineData("2026-10-09T14:59:59Z")] // пятница, 17:59:59 МСК
    public void School_time_is_not_off_hours(string utc)
    {
        Assert.False(Policy.IsOffHours(DateTimeOffset.Parse(utc)));
    }

    [Theory]
    [InlineData("2026-10-05T04:59:59Z")] // понедельник, 07:59:59 МСК
    [InlineData("2026-10-05T15:00:00Z")] // понедельник, 18:00 МСК, граница не включительно
    [InlineData("2026-10-05T20:30:00Z")] // понедельник, 23:30 МСК
    [InlineData("2026-10-10T09:00:00Z")] // суббота, 12:00 МСК
    [InlineData("2026-10-11T09:00:00Z")] // воскресенье, 12:00 МСК
    public void Night_and_weekend_are_off_hours(string utc)
    {
        Assert.True(Policy.IsOffHours(DateTimeOffset.Parse(utc)));
    }

    [Fact]
    public void Saturday_can_be_configured_as_school_day()
    {
        var sixDayWeek = new WorkingHoursPolicy(new WorkingHoursOptions { WorkDays = "Mon,Tue,Wed,Thu,Fri,Sat" });
        Assert.False(sixDayWeek.IsOffHours(DateTimeOffset.Parse("2026-10-10T09:00:00Z")));
    }

    [Theory]
    [InlineData(18, 8)]
    [InlineData(8, 8)]
    [InlineData(-1, 10)]
    [InlineData(8, 25)]
    public void Invalid_hours_are_rejected(int start, int end)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new WorkingHoursPolicy(new WorkingHoursOptions { StartHour = start, EndHour = end }));
    }

    [Fact]
    public void Unknown_day_name_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new WorkingHoursPolicy(new WorkingHoursOptions { WorkDays = "Mon,Funday" }));
    }
}
