namespace AccountUsageUI.Core.Profiles;

public readonly record struct ProfileId
{
    private ProfileId(Guid value)
    {
        Value = value;
    }

    public Guid Value { get; }

    public static ProfileId New() => new(Guid.NewGuid());

    public static ProfileId From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "프로필 ID는 비어 있을 수 없습니다.",
                nameof(value));
        }

        return new ProfileId(value);
    }

    public static bool TryParse(string? value, out ProfileId profileId)
    {
        if (Guid.TryParseExact(value, "D", out var parsed) &&
            parsed != Guid.Empty)
        {
            profileId = new ProfileId(parsed);
            return true;
        }

        profileId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}
