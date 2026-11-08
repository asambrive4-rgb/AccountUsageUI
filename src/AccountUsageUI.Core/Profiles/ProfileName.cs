namespace AccountUsageUI.Core.Profiles;

public sealed record ProfileName
{
    private ProfileName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ProfileName Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "프로필 이름은 비어 있을 수 없습니다.",
                nameof(value));
        }

        return new ProfileName(normalized);
    }

    public override string ToString() => Value;
}
