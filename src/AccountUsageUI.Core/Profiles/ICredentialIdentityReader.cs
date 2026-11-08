namespace AccountUsageUI.Core.Profiles;

public interface ICredentialIdentityReader
{
    string? TryReadAccountId(ReadOnlySpan<byte> credential);
}
