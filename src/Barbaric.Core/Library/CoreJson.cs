using System.Text.Json;
using System.Text.Json.Serialization;

namespace Barbaric.Core.Library;

/// <summary>The JSON shape used for everything Core stores as text: camelCase, enums as strings, no nulls.</summary>
public static class CoreJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
