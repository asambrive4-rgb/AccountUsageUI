using System.Collections.ObjectModel;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.App.Presentation;

public sealed class ProfileListItemViewModel : ObservableObject
{
    private string _name;
    private string _status = "준비됨";
    private string _buttonText = "실행";
    private string _accountSubtext = "";
    private bool _isRunEnabled = true;
    private bool _isDeleteEnabled = true;
    private bool _isSwitchAction;
    private bool _isActive;
    private string _usageStatusMessage = "사용량 확인 중...";
    private bool _hasUsageData;
    private ProfileQuotaSnapshot? _lastSnapshot;
    private ProfileQuotaSnapshot? _previousSnapshot;
    private IReadOnlyList<string> _favoriteFilters =
        FavoriteModelsPolicy.DefaultFavoriteModels;

    public ProfileListItemViewModel(
        ProfileId id,
        string name)
    {
        Id = id;
        _name = name;
        RecentModelUsage.Apply(null);
    }

    public ProfileId Id { get; }

    public string Name
    {
        get => _name;
        private set => SetField(ref _name, value);
    }

    public void UpdateName(string name) =>
        Name = name;

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string AccountSubtext
    {
        get => _accountSubtext;
        private set
        {
            if (SetField(ref _accountSubtext, value))
            {
                OnPropertyChanged(nameof(HasAccountSubtext));
            }
        }
    }

    public bool HasAccountSubtext =>
        !string.IsNullOrWhiteSpace(AccountSubtext);

    public bool IsRunEnabled
    {
        get => _isRunEnabled;
        set => SetField(ref _isRunEnabled, value);
    }

    public bool IsDeleteEnabled
    {
        get => _isDeleteEnabled;
        set => SetField(ref _isDeleteEnabled, value);
    }

    public string ButtonText
    {
        get => _buttonText;
        set => SetField(ref _buttonText, value);
    }

    public bool IsSwitchAction
    {
        get => _isSwitchAction;
        set => SetField(ref _isSwitchAction, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    public string UsageSectionHeader => UsageDisplaySettings.SectionHeaderTitle;

    /// <summary>
    /// Antigravity 기본 Usage 팝업과 동일한 그룹/버킷 계층 뷰모델 컬렉션.
    /// </summary>
    public ObservableCollection<QuotaGroupViewModel> QuotaGroups { get; } = [];

    /// <summary>
    /// 평탄화된 주요 버킷/모델 뷰모델 컬렉션 (단위 테스트 및 호환용).
    /// </summary>
    public ObservableCollection<ModelQuotaItemViewModel> FavoriteModelQuotas { get; } = [];

    public ModelQuotaItemViewModel RecentModelUsage { get; } = new();

    public string UsageStatusMessage
    {
        get => _usageStatusMessage;
        private set
        {
            if (SetField(ref _usageStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasUsageStatus));
            }
        }
    }

    public bool HasUsageData
    {
        get => _hasUsageData;
        private set => SetField(ref _hasUsageData, value);
    }

    public bool HasUsageStatus =>
        !string.IsNullOrWhiteSpace(UsageStatusMessage);

    public IReadOnlyList<string> GetDiscoveredModelFamilyNames()
    {
        if (_lastSnapshot is null || _lastSnapshot.ModelList.Count == 0)
        {
            return [];
        }

        return FavoriteModelsPolicy.CollapseByFamily(_lastSnapshot.ModelList)
            .Select(m => m.DisplayName)
            .ToArray();
    }

    public void ApplyFavoriteFilters(IReadOnlyList<string> favoriteFilters)
    {
        _favoriteFilters = favoriteFilters;
        if (_lastSnapshot is not null)
        {
            RebuildModelViews(_lastSnapshot, _previousSnapshot);
        }
    }

    public void ApplyUsageSnapshot(ProfileQuotaSnapshot snapshot)
    {
        if (_lastSnapshot is not null &&
            _lastSnapshot.HasModels &&
            snapshot.HasModels)
        {
            _previousSnapshot = _lastSnapshot;
        }

        _lastSnapshot = snapshot;

        if (!string.IsNullOrWhiteSpace(snapshot.AccountEmail) ||
            !string.IsNullOrWhiteSpace(snapshot.AccountPlan))
        {
            AccountSubtext = string.IsNullOrWhiteSpace(snapshot.AccountPlan)
                ? snapshot.AccountEmail ?? ""
                : string.IsNullOrWhiteSpace(snapshot.AccountEmail)
                    ? snapshot.AccountPlan!
                    : $"{snapshot.AccountEmail} · {snapshot.AccountPlan}";
        }

        RebuildModelViews(snapshot, _previousSnapshot);
        HasUsageData = QuotaGroups.Count > 0;

        UsageStatusMessage = snapshot.Status switch
        {
            ProfileQuotaStatus.Loading =>
                HasUsageData
                    ? "갱신 중..."
                    : "사용량 확인 중...",
            ProfileQuotaStatus.Available => "",
            ProfileQuotaStatus.UnsupportedAuthentication =>
                "Google OAuth 인증 프로필만 사용량을 확인할 수 있습니다.",
            ProfileQuotaStatus.AuthenticationExpired =>
                "인증이 만료되었습니다. 해당 프로필로 다시 로그인하세요.",
            ProfileQuotaStatus.AppServerUnavailable =>
                "Antigravity 사용량 서버에 연결하지 못했습니다.",
            ProfileQuotaStatus.ResponseFormatChanged =>
                "Antigravity 사용량 응답 형식이 변경되었습니다.",
            _ when HasUsageData =>
                $"갱신 실패 · 마지막 확인 {DescribeLastSuccess(snapshot.LastSuccessfulAt)}",
            _ => "사용량을 불러오지 못했습니다."
        };
    }

    private void RebuildModelViews(
        ProfileQuotaSnapshot snapshot,
        ProfileQuotaSnapshot? previousSnapshot)
    {
        var selectedModels = FavoriteModelsPolicy.SelectFavoriteModels(
            snapshot.ModelList,
            _favoriteFilters);

        FavoriteModelQuotas.Clear();
        QuotaGroups.Clear();

        // Antigravity 원본 Tpc 컴포넌트 렌더링 로직:
        // Group 단위로 묶어서:
        // - 그룹 내 버킷이 1개인 경우(예: Gemini 4 Argon):
        //   그룹 헤더를 별도로 두지 않고, 버킷 행의 title = group.displayName ("Gemini 4 Argon"),
        //   detail = cm(bucket) ("Weekly limit"), nested = false로 렌더링한다.
        // - 그룹 내 버킷이 2개 이상인 경우(예: Gemini Models, Claude and GPT models):
        //   그룹 헤더(group.displayName)를 표시하고, 하위 버킷 행들을 title = cm(bucket) ("Weekly limit", "5h limit"),
        //   detail = null, nested = true로 렌더링한다.
        var grouped = selectedModels
            .GroupBy(
                m => !string.IsNullOrWhiteSpace(m.FamilyGroup)
                    ? m.FamilyGroup!
                    : m.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var group in grouped)
        {
            var buckets = group.ToArray();
            if (buckets.Length == 0)
            {
                continue;
            }

            if (buckets.Length == 1)
            {
                var single = buckets[0];
                var hasDistinctWindow = !string.IsNullOrWhiteSpace(single.Window);
                var title = !string.IsNullOrWhiteSpace(group.Key)
                    ? group.Key
                    : single.WindowLabel;
                var detail = hasDistinctWindow && !string.IsNullOrWhiteSpace(group.Key)
                    ? single.WindowLabel
                    : null;

                var itemVm = new ModelQuotaItemViewModel(
                    single,
                    overrideTitle: title,
                    detailLabel: detail,
                    isNested: false);

                var groupVm = new QuotaGroupViewModel(group.Key, hasGroupHeader: false);
                groupVm.Buckets.Add(itemVm);
                QuotaGroups.Add(groupVm);
                FavoriteModelQuotas.Add(itemVm);
            }
            else
            {
                var groupVm = new QuotaGroupViewModel(group.Key, hasGroupHeader: true);
                foreach (var bucket in buckets)
                {
                    var itemVm = new ModelQuotaItemViewModel(
                        bucket,
                        overrideTitle: bucket.WindowLabel,
                        detailLabel: null,
                        isNested: true);
                    groupVm.Buckets.Add(itemVm);
                    FavoriteModelQuotas.Add(itemVm);
                }

                QuotaGroups.Add(groupVm);
            }
        }

        var recent = FavoriteModelsPolicy.SelectRecentlyUsedModel(
            snapshot,
            previousSnapshot,
            _favoriteFilters);
        var recentDetail = recent is not null && !string.IsNullOrWhiteSpace(recent.Window)
            ? recent.WindowLabel
            : null;
        RecentModelUsage.Apply(
            recent,
            overrideTitle: recent?.DisplayName,
            detailLabel: recentDetail,
            isNested: false);
    }

    private static string DescribeLastSuccess(
        DateTimeOffset? lastSuccessfulAt)
    {
        if (lastSuccessfulAt is null)
        {
            return "시각 정보 없음";
        }

        var elapsed = DateTimeOffset.Now - lastSuccessfulAt.Value;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "방금 전";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}분 전";
        }

        return $"{(int)elapsed.TotalHours}시간 전";
    }
}
