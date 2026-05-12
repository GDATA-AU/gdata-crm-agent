using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmAgent.Models;

namespace CrmAgent.Services;

/// <summary>
/// Decrypts encrypted auth blocks sent by the portal.
/// The AES-256-GCM key is SHA-256(raw_api_key) — the same key derivation
/// the portal uses before encrypting.
/// </summary>
public static class AuthDecryptionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static RestApiAuth Decrypt(RestApiAuth encryptedAuth, string rawApiKey)
    {
        if (string.IsNullOrEmpty(encryptedAuth.Payload) ||
            string.IsNullOrEmpty(encryptedAuth.Iv) ||
            string.IsNullOrEmpty(encryptedAuth.AuthTag))
            throw new InvalidOperationException("Encrypted auth block missing payload, iv, or authTag");

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(rawApiKey));
        var iv = Convert.FromBase64String(encryptedAuth.Iv);
        var authTag = Convert.FromBase64String(encryptedAuth.AuthTag);
        var ciphertext = Convert.FromBase64String(encryptedAuth.Payload);

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(iv, ciphertext, authTag, plaintext);

        var json = Encoding.UTF8.GetString(plaintext);
        return JsonSerializer.Deserialize<RestApiAuth>(json, JsonOptions)
            ?? throw new InvalidOperationException("Decrypted auth block is null");
    }
}
