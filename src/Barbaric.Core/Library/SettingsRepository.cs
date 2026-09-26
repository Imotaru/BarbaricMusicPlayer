using System.Text.Json;
using Dapper;

namespace Barbaric.Core.Library;

/// <summary>
/// App settings as JSON values under string keys. They live in the library database so a scratch
/// database (<c>BARBARIC_LIBRARY_DB</c>) also gets its own settings.
/// </summary>
public sealed class SettingsRepository(LibraryDatabase database)
{
    public string? GetRaw(string key)
    {
        using var connection = database.Open();
        return connection.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", new { key });
    }

    public void SetRaw(string key, string json)
    {
        using var connection = database.Open();
        connection.Execute(
            "INSERT INTO settings (key, value) VALUES (@key, @json) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { key, json });
    }

    public void Remove(string key)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM settings WHERE key = @key", new { key });
    }

    /// <summary>Reads a value. A missing or unreadable one comes back as <c>default</c> rather than failing.</summary>
    public T? Get<T>(string key)
    {
        if (GetRaw(key) is not { } json)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, CoreJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public void Set<T>(string key, T value) => SetRaw(key, Serialize(value));

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, CoreJson.Options);
}
