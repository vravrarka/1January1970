using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inventory.Shared.Http;

public static class JsonDefaults
{
    /// <summary>
    /// Перечисления сериализуются строками в snake_case: <c>available</c>, <c>vr_headset</c>, <c>approved</c>.
    /// Числовые значения перечислений не принимаются.
    /// </summary>
    public static readonly JsonStringEnumConverter EnumConverter = new(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);

    public static readonly JsonSerializerOptions Options = Create();

    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(EnumConverter);
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
        return options;
    }
}

public static class EnumNames
{
    public static string ToApi<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Разбор значения перечисления из строки запроса в том же формате, что и JSON.</summary>
    public static bool TryParse<T>(string? raw, [NotNullWhen(true)] out T? value) where T : struct, Enum
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(ToApi(candidate), raw.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        return false;
    }

    public static string Allowed<T>() where T : struct, Enum =>
        string.Join(", ", Enum.GetValues<T>().Select(ToApi));
}
