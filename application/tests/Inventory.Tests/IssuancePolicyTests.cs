using Inventory.Shared.Domain;

namespace Inventory.Tests;

/// <summary>SR-08 / D-02 / T-08: не более двух устройств, все разных типов.</summary>
public class IssuancePolicyTests
{
    [Fact]
    public void Allows_first_device()
    {
        var result = IssuancePolicy.Check([], DeviceType.Laptop);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Allows_second_device_of_different_type()
    {
        var result = IssuancePolicy.Check([DeviceType.Laptop], DeviceType.Tablet);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Denies_second_device_of_same_type()
    {
        var result = IssuancePolicy.Check([DeviceType.Laptop], DeviceType.Laptop);
        Assert.False(result.Allowed);
        Assert.Equal(IssuancePolicy.DuplicateTypeCode, result.Code);
    }

    [Fact]
    public void Denies_third_device_even_of_new_type()
    {
        var result = IssuancePolicy.Check([DeviceType.Laptop, DeviceType.Tablet], DeviceType.Camera);
        Assert.False(result.Allowed);
        Assert.Equal(IssuancePolicy.LimitReachedCode, result.Code);
    }

    [Fact]
    public void Limit_check_takes_precedence_over_type_check()
    {
        var result = IssuancePolicy.Check([DeviceType.Laptop, DeviceType.Tablet], DeviceType.Laptop);
        Assert.Equal(IssuancePolicy.LimitReachedCode, result.Code);
    }

    [Fact]
    public void Every_type_has_russian_title_used_in_messages()
    {
        foreach (var type in Enum.GetValues<DeviceType>())
        {
            Assert.True(DeviceTypeTitles.Russian.ContainsKey(type), $"Нет названия для {type}");
            var denied = IssuancePolicy.Check([type], type);
            Assert.Contains(DeviceTypeTitles.Russian[type], denied.Message);
        }
    }

    [Fact]
    public void Rejects_null_collection()
    {
        Assert.Throws<ArgumentNullException>(() => IssuancePolicy.Check(null!, DeviceType.Laptop));
    }
}
