using System.Text.Json;

namespace Barbaric.Core.Library;

/// <summary>
/// Passes a list of ids to SQL as a single JSON parameter, read with <c>json_each(@ids)</c>.
/// Selections can hold the whole library, far more than SQLite's limit on query parameters.
/// </summary>
internal static class IdList
{
    public static string ToJson(IEnumerable<long> ids) => JsonSerializer.Serialize(ids.Distinct());
}
