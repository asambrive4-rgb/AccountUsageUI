namespace AccountUsageUI.Core.Profiles;

public sealed record Profile
{
    public Profile(ProfileId id, ProfileName name)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "프로필 ID는 비어 있을 수 없습니다.",
                nameof(id));
        }

        ArgumentNullException.ThrowIfNull(name);
        Id = id;
        Name = name;
    }

    public ProfileId Id { get; }

    public ProfileName Name { get; }
}
