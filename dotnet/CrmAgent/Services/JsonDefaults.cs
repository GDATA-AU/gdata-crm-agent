using System.Text.Json;

namespace CrmAgent.Services;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for portal wire (de)serialization —
/// camelCase property names, case-insensitive matching.
/// </summary>
internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
