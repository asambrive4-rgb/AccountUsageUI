namespace AccountUsageUI.Core.Profiles;

internal sealed class ProfileCreationValidator
{
    private readonly IProfileStore _profileStore;

    public ProfileCreationValidator(IProfileStore profileStore)
    {
        _profileStore = profileStore;
    }

    public async Task<ProfileCreationValidationResult> ValidateAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ProfileName profileName;

        try
        {
            profileName = ProfileName.Create(name);
        }
        catch (ArgumentException)
        {
            return new ProfileCreationValidationResult(
                ProfileCreationValidationStatus.InvalidName);
        }

        var stored = await _profileStore.ReadAllAsync(cancellationToken);
        if (stored.Issues.Count > 0)
        {
            return new ProfileCreationValidationResult(
                ProfileCreationValidationStatus.StorageNeedsAttention);
        }

        if (stored.Profiles.Any(
                profile => StringComparer.OrdinalIgnoreCase.Equals(
                    profile.Name.Value,
                    profileName.Value)))
        {
            return new ProfileCreationValidationResult(
                ProfileCreationValidationStatus.DuplicateName);
        }

        return new ProfileCreationValidationResult(
            ProfileCreationValidationStatus.Valid,
            profileName);
    }
}
