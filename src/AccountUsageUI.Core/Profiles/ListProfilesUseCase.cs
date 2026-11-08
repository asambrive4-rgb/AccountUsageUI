namespace AccountUsageUI.Core.Profiles;

public sealed class ListProfilesUseCase
{
    private readonly IProfileStore _profileStore;

    public ListProfilesUseCase(IProfileStore profileStore)
    {
        _profileStore = profileStore;
    }

    public Task<ProfileStoreReadResult> ExecuteAsync(
        CancellationToken cancellationToken)
    {
        return _profileStore.ReadAllAsync(cancellationToken);
    }
}
