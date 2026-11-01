namespace AuroraCore.Utils;

/// <summary>
/// Centralized JSON options. Mojang metadata uses camelCase; we match it and
/// stay case-insensitive for robustness against minor schema drift.
/// </summary>
public static class JsonHelper
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Failed to deserialize JSON to {typeof(T).Name}");

    public static T? DeserializeNullable<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeIndented<T>(T value)
    {
        var indented = new JsonSerializerOptions(Options) { WriteIndented = true };
        return JsonSerializer.Serialize(value, indented);
    }
}
