namespace AccountUsageUI.Core.Profiles;

public enum ProfileStoreIssueCode
{
    CorruptMetadata,
    ProfileIdMismatch,
    MissingCredential,
    UnreadableCredential,
    UnsafeStorageEntry
}

public sealed record ProfileStoreIssue(
    ProfileStoreIssueCode Code);

public sealed record ProfileStoreReadResult(
    IReadOnlyList<Profile> Profiles,
    IReadOnlyList<ProfileStoreIssue> Issues);

public interface IProfileStore
{
    Task<ProfileStoreReadResult> ReadAllAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(
        Profile profile,
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken);

    Task<byte[]> ReadCredentialAsync(
        ProfileId profileId,
        CancellationToken cancellationToken);

    Task ReplaceCredentialAsync(
        ProfileId profileId,
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken) =>
        Task.FromException(
            new NotSupportedException(
                "이 프로필 저장소는 인증 갱신을 지원하지 않습니다."));

    Task DeleteAsync(
        ProfileId profileId,
        CancellationToken cancellationToken);
}
