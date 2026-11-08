using System.Text.RegularExpressions;
using AccountUsageUI.Core.Profiles;

namespace AccountUsageUI.Core.Usage;

public enum QuotaDisplayLevel
{
    Healthy,
    Warning,
    Danger,
    Dead
}

/// <summary>
/// Antigravity Usage 팝업의 표시 문구 및 포맷 규칙 정의.
/// 하드코딩 대신 이 클래스의 변수/속성을 통해 렌더링하며,
/// 서버 응답의 동적 값(group.displayName, bucket.window, bucket.description)을 우선 반영한다.
/// </summary>
public static partial class UsageDisplaySettings
{
    public static string SectionHeaderTitle { get; set; } = "Usage";

    public static string PercentLeftSuffix { get; set; } = "% left";

    public static string ResetsInPrefix { get; set; } = "Resets in ";

    public static string ResetsLessThanOneMinuteText { get; set; } = "Resets in <1m";

    public static string DailyWindowLabel { get; set; } = "Daily limit";

    public static string WeeklyWindowLabel { get; set; } = "Weekly limit";

    public static string WindowLimitSuffix { get; set; } = " limit";

    /// <summary>
    /// Antigravity 프론트엔드 cm(bucket) 함수와 동일한 규칙으로 window 문자열을 표시 라벨로 변환한다.
    /// - "daily" -> DailyWindowLabel ("Daily limit")
    /// - "weekly" -> WeeklyWindowLabel ("Weekly limit")
    /// - "5h", "30m" 등 ^\d+[smhd]$ 패턴 -> "{window} limit" ("5h limit")
    /// - 그 외 -> bucket.DisplayName
    /// </summary>
    public static string FormatWindowLabel(string? window, string? fallbackDisplayName)
    {
        var normalized = window?.Trim().ToLowerInvariant() ?? "";
        if (string.Equals(normalized, "daily", StringComparison.Ordinal))
        {
            return DailyWindowLabel;
        }

        if (string.Equals(normalized, "weekly", StringComparison.Ordinal))
        {
            return WeeklyWindowLabel;
        }

        if (!string.IsNullOrEmpty(normalized) && DurationWindowPattern().IsMatch(normalized))
        {
            return $"{normalized}{WindowLimitSuffix}";
        }

        return string.IsNullOrWhiteSpace(fallbackDisplayName)
            ? normalized
            : fallbackDisplayName.Trim();
    }

    /// <summary>
    /// Antigravity 프론트엔드 hna(bucket) 함수와 동일한 규칙으로 리셋 서브텍스트를 생성한다.
    /// bucket.Description이 비어 있으면(예: 100% left 상태) null을 반환하여 리셋 시간을 숨긴다.
    /// </summary>
    public static string? FormatResetSubtext(
        string? description,
        DateTimeOffset? resetsAt,
        DateTimeOffset? nowOverride = null)
    {
        if (string.IsNullOrWhiteSpace(description) || resetsAt is null)
        {
            return null;
        }

        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var diffMs = (resetsAt.Value - now).TotalMilliseconds;
        if (diffMs <= 0)
        {
            return ResetsLessThanOneMinuteText;
        }

        var totalMinutes = (long)Math.Floor(diffMs / 60000.0);
        var days = totalMinutes / 1440;
        var hours = (totalMinutes % 1440) / 60;
        var mins = totalMinutes % 60;

        string durationPart;
        if (days > 0)
        {
            durationPart = hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }
        else if (hours > 0)
        {
            durationPart = mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
        }
        else if (mins > 0)
        {
            durationPart = $"{mins}m";
        }
        else
        {
            durationPart = "<1m";
        }

        return $"{ResetsInPrefix}{durationPart}";
    }

    public static string FormatPercentLeft(double remainingFraction)
    {
        var pct = Math.Clamp(
            (int)Math.Round(remainingFraction * 100.0, MidpointRounding.AwayFromZero),
            0,
            100);
        return $"{pct}{PercentLeftSuffix}";
    }

    [GeneratedRegex(@"^\d+[smhd]$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationWindowPattern();
}

public sealed record ModelQuotaWindow(
    string ModelId,
    string DisplayName,
    double RemainingFraction,
    DateTimeOffset? ResetsAt,
    string? FamilyGroup = null,
    string? Window = null,
    string? Description = null,
    bool Disabled = false)
{
    public int RemainingPercent =>
        Math.Clamp(
            (int)Math.Round(
                RemainingFraction * 100.0,
                MidpointRounding.AwayFromZero),
            0,
            100);

    public int UsedPercent =>
        Math.Clamp(100 - RemainingPercent, 0, 100);

    public string WindowLabel =>
        UsageDisplaySettings.FormatWindowLabel(Window, DisplayName);

    public string? ResetSubtext =>
        UsageDisplaySettings.FormatResetSubtext(Description, ResetsAt);

    public QuotaDisplayLevel DisplayLevel =>
        RemainingPercent switch
        {
            0 => QuotaDisplayLevel.Dead,
            < 20 => QuotaDisplayLevel.Danger,
            < 40 => QuotaDisplayLevel.Warning,
            _ => QuotaDisplayLevel.Healthy
        };
}

public enum ProfileQuotaStatus
{
    Loading,
    Available,
    UnsupportedAuthentication,
    AuthenticationExpired,
    AppServerUnavailable,
    ResponseFormatChanged,
    Failed
}

public sealed record ProfileQuotaSnapshot(
    ProfileId ProfileId,
    ProfileQuotaStatus Status,
    IReadOnlyList<ModelQuotaWindow>? Models = null,
    string? AccountEmail = null,
    string? AccountPlan = null,
    string? LastSelectedModelId = null,
    string? MostRecentlyUsedModelId = null,
    DateTimeOffset? LastSuccessfulAt = null)
{
    public IReadOnlyList<ModelQuotaWindow> ModelList =>
        Models ?? [];

    public bool HasModels =>
        ModelList.Count > 0;
}

public sealed record ProfileQuotaReadResult(
    ProfileQuotaStatus Status,
    IReadOnlyList<ModelQuotaWindow> Models,
    string? AccountEmail = null,
    string? AccountPlan = null,
    string? LastSelectedModelId = null,
    string? MostRecentlyUsedModelId = null,
    byte[]? RefreshedCredential = null);
