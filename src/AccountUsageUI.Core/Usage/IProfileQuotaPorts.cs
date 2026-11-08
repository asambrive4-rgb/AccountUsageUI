using AccountUsageUI.Core.Profiles;

namespace AccountUsageUI.Core.Usage;

public interface IProfileQuotaReader : IDisposable
{
    Task<ProfileQuotaReadResult> ReadAsync(
        ProfileId profileId,
        ReadOnlyMemory<byte> credential,
        bool isActiveProfile,
        CancellationToken cancellationToken);

    void StopActiveSession();
}

public sealed record CachedProfileUsageEntry(
    ProfileId ProfileId,
    ProfileQuotaStatus Status,
    IReadOnlyList<ModelQuotaWindow> Models,
    string? AccountEmail,
    string? AccountPlan,
    string? LastSelectedModelId,
    string? MostRecentlyUsedModelId,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset CapturedAt)
{
    public ProfileQuotaSnapshot ToSnapshot() =>
        new(
            ProfileId,
            Status,
            Models,
            AccountEmail,
            AccountPlan,
            LastSelectedModelId,
            MostRecentlyUsedModelId,
            LastSuccessfulAt);

    public static CachedProfileUsageEntry FromSnapshot(
        ProfileQuotaSnapshot snapshot,
        DateTimeOffset capturedAt) =>
        new(
            snapshot.ProfileId,
            snapshot.Status,
            snapshot.ModelList,
            snapshot.AccountEmail,
            snapshot.AccountPlan,
            snapshot.LastSelectedModelId,
            snapshot.MostRecentlyUsedModelId,
            snapshot.LastSuccessfulAt,
            capturedAt);
}

public interface IProfileUsageSnapshotCache
{
    Task LoadAsync(CancellationToken cancellationToken);

    bool TryGet(
        ProfileId profileId,
        out CachedProfileUsageEntry entry);

    Task SetAsync(
        CachedProfileUsageEntry entry,
        CancellationToken cancellationToken);

    Task RemoveAsync(
        ProfileId profileId,
        CancellationToken cancellationToken);

    Task RemoveMissingAsync(
        IReadOnlyCollection<ProfileId> keep,
        CancellationToken cancellationToken);
}
