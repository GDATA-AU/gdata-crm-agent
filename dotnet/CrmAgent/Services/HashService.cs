using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrmAgent.Services;

/// <summary>
/// SHA-256 row hashing for change detection.
/// Matches the portal's <c>computeRowHash</c> logic exactly.
/// </summary>
public static class HashService
{
    /// <summary>
    /// Compute a SHA-256 hash from selected fields of a row.
    /// The hash is computed as: SHA-256( field1|field2|field3|... )
    /// where each field value is trimmed (if a string) and null/undefined
    /// values are represented as empty strings.
    /// </summary>
    public static string ComputeRowHash(Dictionary<string, object?> row, string[] hashFields)
    {
        var parts = new string[hashFields.Length];
        for (var i = 0; i < hashFields.Length; i++)
        {
            if (row.TryGetValue(hashFields[i], out var val) && val is not null)
            {
                parts[i] = Convert.ToString(val)?.Trim() ?? "";
            }
            else
            {
                parts[i] = "";
            }
        }

        var input = string.Join("|", parts);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Compute row hash from a <see cref="JsonElement"/> row (used when
    /// deserializing API responses without materializing to dictionaries first).
    /// </summary>
    public static string ComputeRowHash(JsonElement row, string[] hashFields)
    {
        var parts = new string[hashFields.Length];
        for (var i = 0; i < hashFields.Length; i++)
        {
            if (row.TryGetProperty(hashFields[i], out var prop) &&
                prop.ValueKind != JsonValueKind.Null &&
                prop.ValueKind != JsonValueKind.Undefined)
            {
                parts[i] = prop.ToString().Trim();
            }
            else
            {
                parts[i] = "";
            }
        }

        var input = string.Join("|", parts);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes);
    }
}
