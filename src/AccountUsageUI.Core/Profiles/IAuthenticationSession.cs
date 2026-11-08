namespace AccountUsageUI.Core.Profiles;

public interface IAuthenticationSession
{
    Task<bool> HasPendingRecoveryAsync(
        CancellationToken cancellationToken);

    Task PrepareForLoginAsync(
        CancellationToken cancellationToken);

    Task PrepareForProfileAsync(
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken);

    Task<byte[]?> ReadCurrentCredentialAsync(
        CancellationToken cancellationToken);

    Task RestorePreviousAsync(
        CancellationToken cancellationToken);

    Task ClearRecoveryAsync(
        CancellationToken cancellationToken);
}
