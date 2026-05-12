using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmAgent.Models;
using CrmAgent.Services;

namespace CrmAgent.Tests;

public class AuthDecryptionServiceTests
{
    private const string TestApiKey = "test-api-key-12345";

    /// <summary>
    /// Encrypt an auth object the same way the portal does (AES-256-GCM, key = SHA-256(apiKey)).
    /// </summary>
    private static RestApiAuth EncryptAuth(object plainAuth, string apiKey)
    {
        var json = JsonSerializer.Serialize(plainAuth, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        var iv = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var ciphertext = new byte[plaintext.Length];
        var authTag = new byte[16];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(iv, plaintext, ciphertext, authTag);

        return new RestApiAuth
        {
            Type = AuthType.Encrypted,
            Payload = Convert.ToBase64String(ciphertext),
            Iv = Convert.ToBase64String(iv),
            AuthTag = Convert.ToBase64String(authTag),
        };
    }

    [Fact]
    public void Decrypts_OAuth2Password_RoundTrip()
    {
        var original = new
        {
            Type = "oauth2-password",
            TokenUrl = "https://auth.example.com/token",
            ClientId = "my-client",
            ClientSecret = "super-secret",
            Username = "user@example.com",
            Password = "p@ssw0rd!",
        };

        var encrypted = EncryptAuth(original, TestApiKey);
        var decrypted = AuthDecryptionService.Decrypt(encrypted, TestApiKey);

        Assert.Equal(AuthType.OAuth2Password, decrypted.Type);
        Assert.Equal("https://auth.example.com/token", decrypted.TokenUrl);
        Assert.Equal("my-client", decrypted.ClientId);
        Assert.Equal("super-secret", decrypted.ClientSecret);
        Assert.Equal("user@example.com", decrypted.Username);
        Assert.Equal("p@ssw0rd!", decrypted.Password);
    }

    [Fact]
    public void Decrypts_Bearer_RoundTrip()
    {
        var original = new
        {
            Type = "bearer",
            Token = "eyJhbGciOiJSUzI1NiJ9.test-token",
        };

        var encrypted = EncryptAuth(original, TestApiKey);
        var decrypted = AuthDecryptionService.Decrypt(encrypted, TestApiKey);

        Assert.Equal(AuthType.Bearer, decrypted.Type);
        Assert.Equal("eyJhbGciOiJSUzI1NiJ9.test-token", decrypted.Token);
    }

    [Fact]
    public void Throws_With_Wrong_ApiKey()
    {
        var original = new { Type = "bearer", Token = "token123" };
        var encrypted = EncryptAuth(original, TestApiKey);

        Assert.ThrowsAny<CryptographicException>(
            () => AuthDecryptionService.Decrypt(encrypted, "wrong-api-key"));
    }

    [Fact]
    public void Throws_When_Payload_Missing()
    {
        var auth = new RestApiAuth
        {
            Type = AuthType.Encrypted,
            Iv = Convert.ToBase64String(new byte[12]),
            AuthTag = Convert.ToBase64String(new byte[16]),
        };

        Assert.Throws<InvalidOperationException>(
            () => AuthDecryptionService.Decrypt(auth, TestApiKey));
    }
}
