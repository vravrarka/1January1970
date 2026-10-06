using System.Text.Json;
using Inventory.DeviceService.Endpoints;
using Inventory.Shared.Domain;
using Inventory.Shared.Http;

namespace Inventory.Tests;

public class EnumNamesAndLocksTests
{
    [Theory]
    [InlineData("available", DeviceStatus.Available)]
    [InlineData("ISSUED", DeviceStatus.Issued)]
    [InlineData(" maintenance ", DeviceStatus.Maintenance)]
    public void Parses_status_in_api_format(string raw, DeviceStatus expected)
    {
        Assert.True(EnumNames.TryParse<DeviceStatus>(raw, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("broken")]
    public void Rejects_unknown_status(string? raw)
    {
        Assert.False(EnumNames.TryParse<DeviceStatus>(raw, out _));
    }

    [Fact]
    public void Multi_word_type_uses_snake_case()
    {
        Assert.Equal("vr_headset", EnumNames.ToApi(DeviceType.VrHeadset));
        Assert.True(EnumNames.TryParse<DeviceType>("vr_headset", out var parsed));
        Assert.Equal(DeviceType.VrHeadset, parsed);
    }

    [Fact]
    public void Json_uses_same_enum_format_and_refuses_numbers()
    {
        var json = JsonSerializer.Serialize(new { type = DeviceType.VrHeadset }, JsonDefaults.Options);
        Assert.Equal("{\"type\":\"vr_headset\"}", json);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CreateDeviceRequest>("{\"name\":\"x\",\"type\":1}", JsonDefaults.Options));
    }

    [Fact]
    public void Student_lock_key_is_stable_and_distinct()
    {
        var a = Guid.Parse("8f2c7a0e-2f7e-4a8b-9b1a-2f0c6d1e5a11");
        var b = Guid.Parse("1d4b3c2a-9e8f-4d7c-8b6a-5e4f3d2c1b00");

        Assert.Equal(DeviceLocks.StudentLockKey(a), DeviceLocks.StudentLockKey(a));
        Assert.NotEqual(DeviceLocks.StudentLockKey(a), DeviceLocks.StudentLockKey(b));
    }
}
