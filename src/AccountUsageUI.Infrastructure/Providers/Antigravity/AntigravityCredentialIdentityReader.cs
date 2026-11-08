using System.Security.Cryptography;
using System.Text.Json;
using AccountUsageUI.Core.Profiles;

namespace AccountUsageUI.Infrastructure.Providers.Antigravity;

public sealed class AntigravityCredentialIdentityReader
    : ICredentialIdentityReader
{
    public string? TryReadAccountId(ReadOnlySpan<byte> credential)
    {
        var copy = credential.ToArray();
        try
        {
            using var document = JsonDocument.Parse(copy);
            var root = document.RootElement;

            // 1. Bundle format: {"keyringJson": "...", ...}
            if (root.TryGetProperty("keyringJson", out var keyringProp) &&
                keyringProp.ValueKind == JsonValueKind.String)
            {
                var innerJson = keyringProp.GetString();
                if (!string.IsNullOrWhiteSpace(innerJson))
                {
                    var fromInner = TryExtractIdentityFromKeyringJson(innerJson);
                    if (fromInner is not null)
                    {
                        return fromInner;
                    }
                }
            }

            // 2. Direct keyring JSON format: {"token": {"refresh_token": "..."}, "id_token_email": "..."}
            return TryExtractIdentityFromElement(root);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static string? TryExtractIdentityFromKeyringJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryExtractIdentityFromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryExtractIdentityFromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("id_token_email", out var emailProp) &&
            emailProp.ValueKind == JsonValueKind.String)
        {
            var email = emailProp.GetString();
            if (!string.IsNullOrWhiteSpace(email))
            {
                return email.Trim().ToLowerInvariant();
            }
        }

        if (root.TryGetProperty("token", out var tokenProp) &&
            tokenProp.ValueKind == JsonValueKind.Object &&
            tokenProp.TryGetProperty("refresh_token", out var refreshProp) &&
            refreshProp.ValueKind == JsonValueKind.String)
        {
            var refreshToken = refreshProp.GetString();
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                var hash = SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(refreshToken));
                return $"rt:{Convert.ToHexString(hash)}";
            }
        }

        return null;
    }
}
