using System.Text.Json;

namespace Barbaric.App.Bridge;

/// <summary>Readers for the <c>params</c> object of bridge calls.</summary>
public static class JsonParams
{
    public static long GetId(this JsonElement p, string name = "id") => p.GetProperty(name).GetInt64();

    public static string GetText(this JsonElement p, string name) =>
        p.GetProperty(name).GetString() ?? throw new ArgumentException($"'{name}' is required.");

    public static string? GetOptionalText(this JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads a whole number; a missing or null property reads as null.</summary>
    public static long? GetOptionalNumber(this JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (long)Math.Round(value.GetDouble())
            : null;

    /// <summary>Reads an array of ids; a missing property reads as empty.</summary>
    public static List<long> GetIds(this JsonElement p, string name = "trackIds") =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(e => e.GetInt64())]
            : [];
}
